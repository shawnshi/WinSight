// swift-tools-version: 6.2
import PackageDescription

#if !os(Windows)
fatalError("This source tree supports Windows only. Build with the Windows Swift toolchain.")
#endif

let vendor = "\(Context.packageDirectory)/Vendor"
let package = Package(
    name: "WinSight",
    products: [
        .executable(name: "winsight", targets: ["winsight"]),
        .library(name: "WinSightKit", targets: ["WinSightKit"]),
    ],
    targets: [
        .target(name: "CSQLite", path: "Vendor/sqlite", cSettings: [
            .define("SQLITE_ENABLE_FTS5"), .define("SQLITE_THREADSAFE", to: "1"), .define("SQLITE_DQS", to: "0"),
            .define("SQLITE_OMIT_LOAD_EXTENSION"), .unsafeFlags(["-w"]),
        ]),
        .target(name: "CStb", path: "Vendor/stb", cSettings: [
            .define("STBI_WINDOWS_UTF8"), .define("STBIW_WINDOWS_UTF8"), .unsafeFlags(["-w"]),
        ]),
        .target(name: "CMiniz", path: "Vendor/miniz", cSettings: [.unsafeFlags(["-w"])]),
        .systemLibrary(name: "CPDFium", path: "Sources/CPDFium"),
        .target(
            name: "WinSightKit", dependencies: ["CSQLite", "CStb", "CMiniz", "CPDFium"],
            linkerSettings: [.unsafeFlags(["-L\(vendor)/pdfium/lib"])]
        ),
        .executableTarget(name: "winsight", dependencies: ["WinSightKit"]),
        .testTarget(name: "WinSightKitTests", dependencies: ["WinSightKit", "CMiniz"], exclude: ["Pictures", "Videos"]),
        .testTarget(name: "WindowsMLRuntimeTests", dependencies: ["WinSightKit"], exclude: ["Fixtures"]),
    ]
)
