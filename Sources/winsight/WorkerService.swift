import WinSightKit
import Foundation

/// Standalone tooling envelope; backend selection matches search/index services.
/// Preserve this CLI's legacy `ok` semantics: inspect each entry in `errors` for partial failures.
func workerService(_ args: Arguments) -> Int32 {
    let embedder: any Embedder
    do {
        embedder = try makeEmbedder(textOnly: args.flags.contains("--text-only"))
    } catch {
        emit(["ready": false, "error": "\(error)"])
        return 1
    }
    let info = embedder.info
    var infoFields: [String: Any] = [
        "model": info.model, "revision": info.revision, "runtime": info.runtime, "dtype": info.dtype, "dim": info.dim,
        "text_only": info.textOnly, "load_seconds": info.loadSeconds ?? 0,
    ]
    if let profile = info.profileId { infoFields["profile_id"] = profile }
    if let profile = info.performanceProfileId { infoFields["performance_profile_id"] = profile }
    emit(["ready": true, "info": infoFields])
    while let line = readLine() {
        guard let data = line.data(using: .utf8),
              let request = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
        let id: Any = request["id"] ?? NSNull()
        let inputs = request["inputs"] as? [String] ?? []
        do {
            let embeddings: [Embedding]
            switch request["op"] as? String {
            case "quit":
                emit(["id": id, "ok": true])
                embedder.close()
                return 0
            case "info":
                if let host = embedder as? WindowsMLEmbedder { infoFields = try host.runtimeInfo() }
                emit(["id": id, "ok": true, "info": infoFields])
                continue
            case "text":
                embeddings = try embedder.embedTexts(inputs).map { Embedding(vector: $0, error: nil) }
            case "image":
                embeddings = try embedder.embedImages(inputs, budget: request["budget"] as? Int ?? 280)
            case "audio":
                embeddings = try embedder.embedAudio(inputs)
            case let op:
                throw CLIError("unknown op \(op ?? "(none)")")
            }
            guard embeddings.count == inputs.count else { throw CLIError("embedding item count differs") }
            // Float32 little-endian, one row per input; an input that failed gets zeros and a message.
            let floats = embeddings.flatMap { $0.vector ?? [Float](repeating: 0, count: info.dim) }
            emit(["id": id, "ok": true, "dim": info.dim, "count": inputs.count,
                  "vectors": floats.withUnsafeBufferPointer { Data(buffer: $0) }.base64EncodedString(),
                  "errors": embeddings.map { $0.error.map { $0 as Any } ?? NSNull() }])
        } catch {
            emit(["id": id, "ok": false, "error": String("\(error)".prefix(500))])
        }
    }
    embedder.close()
    return 0
}
