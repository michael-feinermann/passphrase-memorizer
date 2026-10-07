// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MnemonicStory", defaultLocalization: "en", platforms: [.macOS(.v14)],
    products: [.executable(name: "MnemonicStoryApp", targets: ["MnemonicStoryApp"])],
    targets: [
        .target(name: "MnemonicStoryCore", resources: [.process("Resources")]),
        .executableTarget(name: "MnemonicStoryApp", dependencies: ["MnemonicStoryCore"]),
        .testTarget(name: "MnemonicStoryCoreTests", dependencies: ["MnemonicStoryCore"]),
        .testTarget(name: "MnemonicStoryAppTests", dependencies: ["MnemonicStoryApp"])
    ]
)
