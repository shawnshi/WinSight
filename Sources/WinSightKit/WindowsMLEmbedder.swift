import Foundation

/// Sole inference backend. No network listener, model download or index mutation in this adapter.
public final class WindowsMLEmbedder: Embedder {
    public private(set) var info = WorkerInfo(model: "", revision: "", runtime: "", dtype: "", dim: 768,
                                              textOnly: true, loadSeconds: nil)
    private let process: Process
    private let input: Pipe
    private let output: Pipe
    private let replies: ReplyReader
    private let log: URL?
    private var errorHandle: FileHandle?
    private var nextID = 1
    private var capabilities = Capabilities(text: false, image: false, audio: false)
    private let requestTimeout: TimeInterval

    private struct Capabilities: Decodable {
        let text: Bool
        let image: Bool
        let audio: Bool
    }
    private struct Ready: Decodable {
        let ready: Bool
        let info: WorkerInfo?
        let capabilities: Capabilities?
        let error: String?
        let `protocol`: Int?
    }
    private struct Request: Encodable {
        let id: Int
        let op: String
        let inputs: [String]?
        let budget: Int?
    }
    private struct Reply: Decodable {
        let id: Int
        let ok: Bool
        let dim: Int?
        let count: Int?
        let vectors: String?
        let errors: [String?]?
        let error: String?
        let performanceProfileId: String?
    }

    public init(executable: URL, models: URL, manifest: URL, textOnly: Bool, device: String = "auto",
                log: URL? = nil, requestTimeout: TimeInterval = 120) throws {
        let process = Process(), input = Pipe(), output = Pipe()
        self.process = process
        self.input = input
        self.output = output
        self.log = log
        self.requestTimeout = requestTimeout
        replies = ReplyReader(handle: output.fileHandleForReading)
        guard requestTimeout.isFinite, requestTimeout > 0 else { throw WorkerError.failed("invalid inference timeout") }
        process.executableURL = executable
        process.arguments = ["--stdio", "--models", models.path, "--manifest", manifest.path, "--device", device]
            + (textOnly ? ["--text-only"] : [])
        process.qualityOfService = textOnly ? .userInitiated : .utility
        process.standardInput = input
        process.standardOutput = output
        if let log {
            if !FileManager.default.fileExists(atPath: log.path) {
                _ = FileManager.default.createFile(atPath: log.path, contents: nil)
            }
            errorHandle = try FileHandle(forWritingTo: log)
            _ = try errorHandle?.seekToEnd()
        }
        process.standardError = errorHandle ?? FileHandle.standardError
        do {
            try process.run()
            try? output.fileHandleForWriting.close()
            try? input.fileHandleForReading.close()
            replies.start()
            let ready = try decoder().decode(Ready.self, from: replies.receive(timeout: 30))
            guard ready.ready, ready.protocol == 1, let reported = ready.info,
                  let advertised = ready.capabilities, advertised.text,
                  reported.dim == 768, let profile = reported.profileId, profile.utf8.count == 64,
                  profile.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
                throw WorkerError.failed(ready.error ?? "invalid Windows ML readiness/profile/capability response")
            }
            guard reported.textOnly == textOnly else { throw WorkerError.failed("host text-only mode differs") }
            info = reported
            capabilities = advertised
        } catch {
            stop(graceful: false)
            throw WorkerError.failed("Windows ML startup: \(error)\(logTail())")
        }
    }

    deinit { close() }

    public func embedTexts(_ texts: [String]) throws -> [[Float]] {
        let values = try send("text", texts, budget: nil)
        return try values.map { value in
            guard let vector = value.vector, value.error == nil else {
                throw WorkerError.failed(value.error ?? "text embedding is missing")
            }
            return vector
        }
    }

    public func embedImages(_ paths: [String], budget: Int) throws -> [Embedding] {
        guard capabilities.image else { throw WorkerError.failed("host does not advertise image support") }
        return try send("image", paths, budget: budget)
    }

    public func embedAudio(_ paths: [String]) throws -> [Embedding] {
        guard capabilities.audio else { throw WorkerError.failed("host does not advertise audio support") }
        return try send("audio", paths, budget: nil)
    }

    /// Catalog preparation and qualification status, without starting another model execution.
    public func runtimeInfo() throws -> [String: Any] {
        let id = nextID
        nextID += 1
        var payload = try JSONEncoder().encode(Request(id: id, op: "info", inputs: nil, budget: nil))
        payload.append(0x0A)
        do {
            let deadline = Date().addingTimeInterval(requestTimeout)
            try PipeWrite(handle: input.fileHandleForWriting, data: payload).wait(until: deadline)
            let data = try replies.receive(timeout: deadline.timeIntervalSinceNow)
            guard let response = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  response["id"] as? Int == id, response["ok"] as? Bool == true,
                  let fields = response["info"] as? [String: Any] else {
                throw WorkerError.failed("invalid inference info response")
            }
            let refreshed = try decoder().decode(WorkerInfo.self, from: JSONSerialization.data(withJSONObject: fields))
            guard refreshed.profileId == info.profileId, refreshed.dim == info.dim else {
                throw WorkerError.failed("host profile changed during info refresh")
            }
            info = refreshed
            return fields
        } catch {
            stop(graceful: false)
            throw WorkerError.died("Windows ML status transport: \(error)")
        }
    }

    public func close() { stop(graceful: true) }

    private func stop(graceful: Bool) {
        if process.isRunning {
            if !graceful { process.terminate() }
            // EOF avoids a potentially blocking quit write to a stalled child.
            try? input.fileHandleForWriting.close()
            let deadline = Date().addingTimeInterval(3)
            while process.isRunning, Date() < deadline { Thread.sleep(forTimeInterval: 0.02) }
            if process.isRunning { process.terminate() }
        }
        replies.close()
        try? errorHandle?.close()
    }

    private func send(_ operation: String, _ values: [String], budget: Int?) throws -> [Embedding] {
        guard !values.isEmpty else { return [] }
        // Existing callers may submit all windows in a long file; the host has a bounded batch contract.
        if values.count > 16 {
            var results: [Embedding] = []
            for start in stride(from: 0, to: values.count, by: 16) {
                results += try send(operation, Array(values[start..<min(start + 16, values.count)]), budget: budget)
            }
            return results
        }
        guard process.isRunning else { throw WorkerError.died("Windows ML host is not running\(logTail())") }
        let id = nextID
        nextID += 1
        let request = try JSONEncoder().encode(Request(id: id, op: operation, inputs: values, budget: budget))
        if request.count > 1024 * 1024 {
            guard values.count > 1 else { throw WorkerError.failed("inference item exceeds 1 MiB JSONL limit") }
            let middle = values.count / 2
            return try send(operation, Array(values[..<middle]), budget: budget)
                + send(operation, Array(values[middle...]), budget: budget)
        }
        let reply: Reply
        do {
            let deadline = Date().addingTimeInterval(requestTimeout)
            var payload = request
            payload.append(0x0A)
            try PipeWrite(handle: input.fileHandleForWriting, data: payload).wait(until: deadline)
            reply = try decoder().decode(Reply.self, from: replies.receive(timeout: deadline.timeIntervalSinceNow))
        } catch {
            stop(graceful: false)
            throw WorkerError.died("Windows ML transport: \(error)\(logTail())")
        }
        guard reply.id == id else { close(); throw WorkerError.failed("inference reply ID differs") }
        if let error = reply.error { throw WorkerError.failed(error) }
        // ok=false with a complete payload is a partial batch, not a request-level transport failure.
        guard reply.dim == 768, reply.count == values.count,
              let errors = reply.errors, errors.count == values.count,
              let encoded = reply.vectors, let data = Data(base64Encoded: encoded),
              data.count == values.count * 768 * MemoryLayout<UInt32>.size,
              reply.ok == errors.allSatisfy({ $0 == nil }) else {
            close()
            throw WorkerError.failed("inference response count, dimension or payload length differs")
        }
        info = WorkerInfo(model: info.model, revision: info.revision, runtime: info.runtime, dtype: info.dtype,
                          dim: info.dim, textOnly: info.textOnly, loadSeconds: info.loadSeconds,
                          profileId: info.profileId, performanceProfileId: reply.performanceProfileId)
        return try data.withUnsafeBytes { bytes in
            try values.indices.map { row in
                if let error = errors[row] { return Embedding(vector: nil, error: error) }
                let vector = (0..<768).map { column in
                    let raw = bytes.loadUnaligned(fromByteOffset: (row * 768 + column) * 4, as: UInt32.self)
                    return Float(bitPattern: UInt32(littleEndian: raw))
                }
                guard vector.allSatisfy(\.isFinite) else { throw WorkerError.failed("non-finite inference vector") }
                let norm = vector.reduce(0.0) { $0 + Double($1) * Double($1) }
                guard abs(norm - 1) < 0.01 else { throw WorkerError.failed("inference vector is not normalized") }
                return Embedding(vector: vector, error: nil)
            }
        }
    }

    private func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }

    private func logTail() -> String {
        guard let log, let handle = try? FileHandle(forReadingFrom: log) else { return "" }
        defer { try? handle.close() }
        guard let end = try? handle.seekToEnd(), (try? handle.seek(toOffset: end > 2048 ? end - 2048 : 0)) != nil,
              let data = try? handle.readToEnd(), !data.isEmpty else { return "" }
        return "; host stderr: " + String(decoding: data, as: UTF8.self)
    }
}

/// Include a potentially stalled pipe write in the same deadline as the response.
private final class PipeWrite: @unchecked Sendable {
    private let handle: FileHandle
    private let data: Data
    private let condition = NSCondition()
    private var complete = false
    private var failure: String?

    init(handle: FileHandle, data: Data) {
        self.handle = handle
        self.data = data
    }

    func wait(until deadline: Date) throws {
        Thread.detachNewThread {
            var message: String?
            do { try self.handle.write(contentsOf: self.data) }
            catch { message = String(describing: error) }
            self.condition.lock()
            self.failure = message
            self.complete = true
            self.condition.signal()
            self.condition.unlock()
        }
        condition.lock()
        defer { condition.unlock() }
        while !complete {
            if !condition.wait(until: deadline) { throw WorkerError.died("inference request write timed out") }
        }
        if let failure { throw WorkerError.died("inference request write failed: \(failure)") }
    }
}

/// One bounded reader thread owns the pipe; NSCondition protects response/EOF handoff to the single caller.
private final class ReplyReader: @unchecked Sendable {
    private let handle: FileHandle
    private let condition = NSCondition()
    private var lines: [Data] = []
    private var failure: String?

    init(handle: FileHandle) { self.handle = handle }

    func start() { Thread.detachNewThread { self.pump() } }

    func receive(timeout: TimeInterval) throws -> Data {
        condition.lock()
        defer { condition.unlock() }
        let deadline = Date().addingTimeInterval(timeout)
        while lines.isEmpty, failure == nil {
            if !condition.wait(until: deadline) { throw WorkerError.died("inference response timed out") }
        }
        guard !lines.isEmpty else { throw WorkerError.died(failure ?? "inference pipe ended") }
        return lines.removeFirst()
    }

    func close() {
        finish("inference pipe closed")
        try? handle.close()
    }

    private func finish(_ message: String) {
        condition.lock()
        if failure == nil { failure = message }
        condition.broadcast()
        condition.unlock()
    }

    private func pump() {
        var buffered = Data()
        do {
            while let chunk = try handle.read(upToCount: 64 * 1024), !chunk.isEmpty {
                buffered.append(chunk)
                guard buffered.count <= 8 * 1024 * 1024 else {
                    finish("inference response exceeds 8 MiB")
                    return
                }
                while let newline = buffered.firstIndex(of: 0x0A) {
                    let line = Data(buffered[..<newline])
                    buffered.removeSubrange(buffered.startIndex...newline)
                    if line.isEmpty { continue }
                    condition.lock()
                    if lines.count >= 16 {
                        failure = "too many unsolicited inference responses"
                        condition.broadcast()
                        condition.unlock()
                        return
                    }
                    lines.append(line)
                    condition.signal()
                    condition.unlock()
                }
            }
            finish(buffered.isEmpty ? "inference host reached EOF" : "inference response was truncated")
        } catch {
            finish("inference pipe read failed: \(error)")
        }
    }
}
