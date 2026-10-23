import Foundation
import Testing
@testable import WinSightKit

/// Source-only additions in this delivery: these checks have not been executed.
struct WindowsMLModelContractTests {
    @Test func multimodalAssetsRemainTogether() {
        let names = Set(ModelFiles.all.map(\.name))
        #expect(names.contains("tokenizer.json"))
        for graph in ["model_q4", "vision_encoder_q4", "audio_encoder_q4"] {
            #expect(names.contains("onnx/\(graph).onnx"))
            #expect(names.contains("onnx/\(graph).onnx_data"))
        }
        #expect(ModelFiles.all.allSatisfy { !$0.name.hasSuffix(".gguf") && $0.bytes > 0 })
        #expect(ModelFiles.all.allSatisfy { $0.sha256.count == 64 })
    }

    @Test func profilesDoNotShareVectorIdentity() {
        func info(_ profile: String?) -> WorkerInfo {
            WorkerInfo(model: ModelFiles.repository, revision: ModelFiles.revision, runtime: "windows-ml-ort",
                       dtype: "q4", dim: 768, textOnly: false, loadSeconds: nil, profileId: profile)
        }
        #expect(info(String(repeating: "a", count: 64)).vectorSource != info(String(repeating: "b", count: 64)).vectorSource)
        #expect(info(nil).vectorSource != info(String(repeating: "a", count: 64)).vectorSource)
    }

    @Test func oldPerformanceCacheDoesNotNameANewProfile() throws {
        let old = Data(#"{"embedding":1,"ocr":1,"device":"cpu"}"#.utf8)
        let speed = try JSONDecoder().decode(MeasuredCosts.Speed.self, from: old)
        #expect(speed.profileId == nil)
    }
}
