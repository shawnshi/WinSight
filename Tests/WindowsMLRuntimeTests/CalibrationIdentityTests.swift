import Foundation
import Testing
@testable import WinSightKit

/// WML-CUTOVER-001 regression source; not executed in the code-only delivery.
struct CalibrationIdentityTests {
    @Test func cachesRequireAVerifiedCurrentIdentity() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = MeasuredCosts.file(in: folder)
        let old = Data(#"{"speed":{"embedding":2,"ocr":1},"tallies":{"image":{"units":20,"seconds":5}}}"#.utf8)
        try old.write(to: file)
        #expect(MeasuredCosts.load(from: folder).speed == nil)
        #expect(MeasuredCosts.load(from: folder, expectedProfile: "a").tallies.isEmpty)

        var current = MeasuredCosts(profileId: "a")
        var speed = MeasuredCosts.Speed(embedding: 2, ocr: 1)
        speed.profileId = "a"
        current.speed = speed
        current.add("image", units: 20, seconds: 5)
        current.save(to: folder)
        #expect(MeasuredCosts.load(from: folder).tallies.isEmpty)
        #expect(MeasuredCosts.load(from: folder, expectedProfile: "b").speed == nil)
        #expect(MeasuredCosts.load(from: folder, expectedProfile: "a").tallies["image"]?.units == 20)

        // Failed/unverified calibration must not turn prior data into a valid current calibration.
        let before = try Data(contentsOf: file)
        MeasuredCosts.setSpeed(MeasuredCosts.Speed(embedding: 1, ocr: 1), in: folder)
        #expect(try Data(contentsOf: file) == before)
        #expect(MeasuredCosts.load(from: folder).speed == nil)

        var next = MeasuredCosts(profileId: "b")
        next.add("image", units: 3, seconds: 1)
        MeasuredCosts.merge(next, into: folder)
        let changed = MeasuredCosts.load(from: folder, expectedProfile: "b")
        #expect(changed.tallies["image"]?.units == 3)
        #expect(changed.speed == nil)
        var unknown = MeasuredCosts()
        unknown.add("image", units: 100, seconds: 1)
        MeasuredCosts.merge(unknown, into: folder)
        #expect(MeasuredCosts.load(from: folder, expectedProfile: "b").tallies["image"]?.units == 3)
    }
}
