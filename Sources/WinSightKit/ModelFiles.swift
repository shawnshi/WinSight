#if canImport(CryptoKit)
import CryptoKit
#endif
import Foundation

/// Fixed ONNX/tokenizer/config assets shared with the Windows ML host's embedded model-manifest.json.
/// Includes audio for both standalone sound and video soundtrack windows.
public enum ModelFiles {
    public static let repository = "onnx-community/embeddinggemma-2-ONNX"
    public static let revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0"

    public struct File: Sendable, Equatable {
        public let name: String
        public let bytes: Int64
        public let sha256: String

        /// Hugging Face's address for this file at the pinned revision (it redirects to a CDN that takes HTTP ranges).
        public var url: URL {
            URL(string: "https://huggingface.co/\(ModelFiles.repository)/resolve/\(ModelFiles.revision)/\(name)")!
        }
    }

    public static let all: [File] = [
        File(name: "config.json", bytes: 5031, sha256: "8d011bfe08b5e345bbe0b81e5c6fd02c381920b345b986047bc2a33ce7b90d1d"),
        File(name: "onnx/audio_encoder_q4.onnx", bytes: 245543, sha256: "c4cce3370e72262280d00293cac038896050a03a8e9a27e10b061ca97510296e"),
        File(name: "onnx/audio_encoder_q4.onnx_data", bytes: 189075968, sha256: "ba9328e6341360974083085b44b2bba265003bda564740f7c4c23ed9928f17e2"),
        File(name: "onnx/model_q4.onnx", bytes: 490742, sha256: "f9eeba97acddf139b8ee2ddf04bc30dceafa88de93fadf74d7644e0d61a477a9"),
        File(name: "onnx/model_q4.onnx_data", bytes: 174028800, sha256: "c3975f2d1ab7a1878ae31a7d7a9b7804a827aff3800b60dfceafce21cac3df49"),
        File(name: "onnx/vision_encoder_q4.onnx", bytes: 159400, sha256: "7ea284226d4938f0ad921ab091f1d80a9ca699aa802984ef5cd5eec4f4761d96"),
        File(name: "onnx/vision_encoder_q4.onnx_data", bytes: 108957696, sha256: "0a9d6c927334f152a33dd90874f65d6ea5228999abe6a450d3f7813677fa704c"),
        File(name: "preprocessor_config.json", bytes: 560, sha256: "9344893f8d0573a46ebb2ca54c03f56d044cea194c8dcfb1f4d652240ac21daa"),
        File(name: "processor_config.json", bytes: 1788, sha256: "168f6a08522f3ce5dea596d94d003af2fd691742d4f41fe1f9d8cce76bfbf69c"),
        File(name: "tokenizer.json", bytes: 32170510, sha256: "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4"),
        File(name: "tokenizer_config.json", bytes: 1599, sha256: "17bd5d6e9364ca49a534e1502076593317c298d4a663623091ed45388f004874"),
    ]
    public static let totalBytes = all.reduce(0) { $0 + $1.bytes }

    /// Explicit override or the normal application model directory; no GGUF/cache fallback.
    public static var installed: URL {
        let env = ProcessInfo.processInfo.environment
        if let path = env["WINSIGHT_ONNX_MODELS"] ?? env["WINSIGHT_MODELS"], !path.isEmpty {
            return URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
        }
        return AppPaths.applicationSupport.appendingPathComponent("Models")
    }

    public static var complete: Bool { missing(in: installed).isEmpty }

    /// The pinned revision's folder in the Hugging Face cache (`hf download` puts the files there): `$HF_HUB_CACHE`,
    /// `$HF_HOME/hub`, or `~/.cache/huggingface/hub`.
    public static var huggingFaceSnapshot: URL {
        let environment = ProcessInfo.processInfo.environment
        let hub = environment["HF_HUB_CACHE"].map { URL(fileURLWithPath: $0) }
            ?? environment["HF_HOME"].map { URL(fileURLWithPath: $0).appendingPathComponent("hub") }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".cache/huggingface/hub")
        return hub.appendingPathComponent("models--\(repository.replacingOccurrences(of: "/", with: "--"))/snapshots/\(revision)")
    }

    /// `file` in the Hugging Face cache, when it's there at the right size (its checksum is for the caller to check).
    public static func cachedCopy(of file: File) -> URL? {
        let url = huggingFaceSnapshot.appendingPathComponent(file.name).resolvingSymlinksInPath()
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value
        return size == file.bytes ? url : nil
    }

    /// The files that aren't in `folder` yet. Only verified files get their final name, so being there is enough.
    public static func missing(in folder: URL) -> [File] {
        all.filter { !FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path) }
    }

    /// The SHA-256 of a file as lowercase hex, streamed in bounded pieces.
    public static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            let done = try autoreleasepool { () -> Bool in
                guard let data = try handle.read(upToCount: 8 << 20), !data.isEmpty else { return true }
                hasher.update(data: data)
                return false
            }
            if done { break }
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
