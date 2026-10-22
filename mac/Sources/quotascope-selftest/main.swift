import Foundation
import QuotaCore

// Checks QuotaCore against the shared fixtures. Builds anywhere Foundation
// does, Windows included:
//   swift run quotascope-selftest [path/to/fixtures]
// The default path is ../fixtures, relative to the mac/ package directory.
let root = CommandLine.arguments.count > 1
    ? URL(fileURLWithPath: CommandLine.arguments[1])
    : URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("../fixtures").standardizedFileURL

var total = 0
do {
    for folder in Fixtures.folders {
        let count = try Fixtures.run(folder: folder, in: root)
        print("fixtures/\(folder): \(count) cases ok")
        total += count
    }
} catch {
    FileHandle.standardError.write(Data("\(error)\n".utf8))
    exit(1)
}
print("\(total) cases ok")
