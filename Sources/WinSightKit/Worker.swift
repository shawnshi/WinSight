import Foundation

/// What the embedding runtime reports about its model; part of the index fingerprint.
public struct WorkerInfo: Codable, Sendable {
    public let model: String
    public let revision: String
    public let runtime: String
    public let dtype: String
    public let dim: Int
    public let textOnly: Bool
    public let loadSeconds: Double?
    public let profileId: String?
    public let performanceProfileId: String?

    public init(model: String, revision: String, runtime: String, dtype: String, dim: Int, textOnly: Bool,
                loadSeconds: Double?, profileId: String? = nil, performanceProfileId: String? = nil) {
        self.model = model
        self.revision = revision
        self.runtime = runtime
        self.dtype = dtype
        self.dim = dim
        self.textOnly = textOnly
        self.loadSeconds = loadSeconds
        self.profileId = profileId
        self.performanceProfileId = performanceProfileId
    }

    /// Legacy identities remain unchanged; ONNX identities also bind tokenizer, media and prompt versions.
    public var vectorSource: String {
        let base = "\(model)@\(revision.prefix(7)) \(runtime) \(dtype)"
        return profileId.map { "\(base) profile=\($0)" } ?? base
    }
}

/// Linked legacy, isolated Windows ML and test embedders implement this interface. One thread at a time.
public protocol Embedder: AnyObject {
    var info: WorkerInfo { get }
    func embedTexts(_ texts: [String]) throws -> [[Float]]
    func embedImages(_ paths: [String], budget: Int) throws -> [Embedding]
    func embedAudio(_ paths: [String]) throws -> [Embedding]
    /// Frees the model. Safe to call twice.
    func close()
}

public enum WorkerError: Error, CustomStringConvertible {
    case notFound(String), failed(String), died(String)

    public var description: String {
        switch self {
        case .notFound(let why): "worker not found: \(why)"
        case .failed(let why): "worker error: \(why)"
        case .died(let why): "worker stopped: \(why)"
        }
    }
}

/// One embedding (or why the file couldn't be embedded).
public struct Embedding: Sendable {
    public let vector: [Float]?
    public let error: String?

    public init(vector: [Float]?, error: String?) {
        self.vector = vector
        self.error = error
    }
}
