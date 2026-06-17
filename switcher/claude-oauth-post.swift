import Foundation

let args = CommandLine.arguments
guard args.count == 2, let url = URL(string: args[1]) else {
    FileHandle.standardError.write(Data("usage: claude-oauth-post <url>\n".utf8))
    exit(64)
}

let body = FileHandle.standardInput.readDataToEndOfFile()
let userAgent = "ClaudeUsageBar/0.6"

var request = URLRequest(url: url)
request.httpMethod = "POST"
request.setValue("application/json", forHTTPHeaderField: "Content-Type")
request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
request.httpBody = body

let semaphore = DispatchSemaphore(value: 0)
var responseData = Data()
var errorText: String?
var statusCode: Int?

let session = URLSession(configuration: .ephemeral)
let task = session.dataTask(with: request) { data, response, error in
    if let data {
        responseData = data
    }
    if let http = response as? HTTPURLResponse {
        statusCode = http.statusCode
    }
    if let error {
        errorText = error.localizedDescription
    }
    semaphore.signal()
}

task.resume()
if semaphore.wait(timeout: .now() + 30) == .timedOut {
    task.cancel()
    FileHandle.standardError.write(Data("request timed out\n".utf8))
    exit(70)
}

if let errorText {
    FileHandle.standardError.write(Data("request failed: \(errorText)\n".utf8))
    exit(70)
}

guard let statusCode else {
    FileHandle.standardError.write(Data("invalid HTTP response\n".utf8))
    exit(70)
}

if statusCode < 200 || statusCode >= 300 {
    let text = String(data: responseData, encoding: .utf8) ?? ""
    FileHandle.standardError.write(Data("Claude API HTTP \(statusCode): \(text.prefix(400))\n".utf8))
    exit(1)
}

FileHandle.standardOutput.write(responseData)
