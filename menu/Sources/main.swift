import AppKit
import Foundation

private func resolveSwitcherPath() -> String? {
    if let override = ProcessInfo.processInfo.environment["CODEX_DESKTOP_SWITCH_PATH"], !override.isEmpty {
        return override
    }
    if let bundled = Bundle.main.resourceURL?.appendingPathComponent("codex-desktop-switch").path,
       FileManager.default.isExecutableFile(atPath: bundled) {
        return bundled
    }
    let commonPaths = [
        "/usr/local/bin/codex-desktop-switch",
        "/opt/homebrew/bin/codex-desktop-switch",
    ]
    return commonPaths.first { FileManager.default.isExecutableFile(atPath: $0) }
}

private let switcherPath = resolveSwitcherPath()

private struct CommandResult {
    let code: Int32
    let stdout: Data
    let stderr: String
}

private func runSwitcher(_ args: [String], input: String? = nil) -> CommandResult {
    guard let switcherPath else {
        return CommandResult(code: 127, stdout: Data(), stderr: "codex-desktop-switch not found")
    }
    let process = Process()
    process.executableURL = URL(fileURLWithPath: switcherPath)
    process.arguments = args
    var environment = ProcessInfo.processInfo.environment
    let fallbackPath = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
    if let existingPath = environment["PATH"], !existingPath.isEmpty {
        environment["PATH"] = "\(fallbackPath):\(existingPath)"
    } else {
        environment["PATH"] = fallbackPath
    }
    process.environment = environment

    let tempDirectory = FileManager.default.temporaryDirectory
    let token = UUID().uuidString
    let outputURL = tempDirectory.appendingPathComponent("codex-desktop-switch-\(token).out")
    let errorURL = tempDirectory.appendingPathComponent("codex-desktop-switch-\(token).err")
    FileManager.default.createFile(atPath: outputURL.path, contents: nil)
    FileManager.default.createFile(atPath: errorURL.path, contents: nil)
    guard let outputHandle = try? FileHandle(forWritingTo: outputURL),
          let errorHandle = try? FileHandle(forWritingTo: errorURL) else {
        try? FileManager.default.removeItem(at: outputURL)
        try? FileManager.default.removeItem(at: errorURL)
        return CommandResult(code: 127, stdout: Data(), stderr: "failed to create temporary output files")
    }

    defer {
        try? outputHandle.close()
        try? errorHandle.close()
        try? FileManager.default.removeItem(at: outputURL)
        try? FileManager.default.removeItem(at: errorURL)
    }

    process.standardOutput = outputHandle
    process.standardError = errorHandle
    if input != nil {
        process.standardInput = Pipe()
    }

    do {
        try process.run()
        if let input, let inputPipe = process.standardInput as? Pipe {
            inputPipe.fileHandleForWriting.write(input.data(using: .utf8) ?? Data())
            inputPipe.fileHandleForWriting.closeFile()
        }
    } catch {
        return CommandResult(code: 127, stdout: Data(), stderr: String(describing: error))
    }

    // Redirect to files instead of pipes. A large doctor/status payload can fill
    // a 64KB pipe and deadlock if the parent waits first; file handles keep
    // output flowing without Swift 6-unfriendly cross-thread buffer mutation.
    process.waitUntilExit()
    try? outputHandle.close()
    try? errorHandle.close()

    let output = (try? Data(contentsOf: outputURL)) ?? Data()
    let errorData = (try? Data(contentsOf: errorURL)) ?? Data()
    let errorText = String(data: errorData, encoding: .utf8) ?? ""
    return CommandResult(code: process.terminationStatus, stdout: output, stderr: errorText)
}

private func parseJSON(_ data: Data) -> [String: Any]? {
    guard !data.isEmpty else { return nil }
    return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
}

private func stringArray(_ value: Any?) -> [String] {
    return (value as? [Any] ?? []).compactMap { $0 as? String }
}

private func processHint(from json: [String: Any]?) -> String {
    guard let json else { return "프로세스 확인 불가" }
    let processes = stringArray(json["processes"]) + stringArray(json["remaining"])
    if processes.contains(where: { $0.contains("codex app-server") }) {
        return "app-server 잔여"
    }
    if processes.contains(where: { $0.contains("Codex Computer Use.app") || $0.contains("SkyComputerUse") }) {
        return "도구 세션 잔여"
    }
    if processes.contains(where: { $0.contains("Codex.app/") }) {
        return "Codex 본앱 잔여"
    }
    if json["process_check"] as? String == "unknown" {
        return "프로세스 확인 불가"
    }
    return "Codex 종료 필요"
}

private func switchFailureMessage(result: CommandResult) -> String {
    let json = parseJSON(result.stdout)
    // Backend exit codes (codex-desktop-switch): 10+ so they never collide
    // with python tracebacks (1) or argparse errors (2).
    if result.code == 10 { return "실패: \(processHint(from: json))" }
    if result.code == 11 { return "실패: keyring 모드" }
    if result.code == 12 { return "실패: 프로필 로그인 필요" }
    if result.code == 13 { return "실패: 프로세스 확인 불가" }
    if let error = json?["error"] as? String {
        if error.contains("source auth.json not found") { return "실패: 프로필 로그인 필요" }
        if error.contains("running") { return "실패: \(processHint(from: json))" }
    }
    return "실패: 전환 불가"
}

private func nowTimeString() -> String {
    let formatter = DateFormatter()
    formatter.dateFormat = "HH:mm"
    return formatter.string(from: Date())
}

private let menuWidth: CGFloat = 310
private let rowHeight: CGFloat = 24
private let usageRefreshMinuteStep = 10

private struct CurrentSnapshot {
    let activeLabel: String
    let matchedProfile: String?
    let authMatch: String
}

final class CodexMenuApp: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()

    private var profiles: [String] = []
    private var activeLabel = "unknown"
    private var matchedProfile: String?
    private var authMatch = "unknown"
    private var switchFeedbackMessage: String?
    private var switchFeedbackGeneration = 0
    private var isSwitching = false
    private var isRefreshingStatus = false
    private let transientFailureMessages: Set<String> = [
        "실패: 상태 확인",
        "실패: usage 갱신",
        "실패: Claude usage",
        "실패: 일부 갱신",
    ]
    private var isRefreshingUsage = false
    private var isRefreshingClaudeUsage = false
    private var lastUsageRefresh: String?
    private var usageRows: [[String: Any]] = []
    private var usageRefreshTimer: Timer?
    private var claudeFiveText: String?
    private var claudeWeekText: String?
    private var claudeFiveUtilization: Double?
    private var claudeWeekUtilization: Double?
    private var claudeFiveResetText: String?
    private var claudeWeekResetText: String?
    private var lastClaudeUsageRefresh: String?
    private var claudeUsageIsStale = false
    private var claudeUsageMessage: String? = "로그인 필요"

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = menu
        menu.autoenablesItems = false
        // Fast file-only read for the initial paint; the full profile list (which
        // may launch the backend) loads asynchronously in refreshAllData.
        profiles = loadConfiguredProfiles()
        rebuildMenu()
        scheduleUsageRefreshTimer()
        refreshAllData(showStatus: false)
    }

    func applicationWillTerminate(_ notification: Notification) {
        usageRefreshTimer?.invalidate()
        usageRefreshTimer = nil
    }

    private func loadCurrentSnapshot() -> CurrentSnapshot? {
        let result = runSwitcher(["current", "--json"])
        guard result.code == 0, let json = parseJSON(result.stdout) else { return nil }
        return CurrentSnapshot(
            activeLabel: json["active_label"] as? String ?? "unknown",
            matchedProfile: json["matched_profile"] as? String,
            authMatch: json["auth_match"] as? String ?? "unknown"
        )
    }

    private func loadProfiles() -> [String] {
        let configured = loadConfiguredProfiles()
        if !configured.isEmpty { return configured }

        let result = runSwitcher(["list", "--json"])
        guard result.code == 0,
              let json = parseJSON(result.stdout),
              let rows = json["profiles"] as? [[String: Any]] else {
            return []
        }
        return rows.compactMap { $0["profile"] as? String }.filter { !$0.isEmpty }
    }

    private func loadConfiguredProfiles() -> [String] {
        let configURL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex-switch/config.json")
        guard let data = try? Data(contentsOf: configURL),
              let json = parseJSON(data),
              let configured = json["profiles"] as? [String] else {
            return []
        }
        return configured.filter { !$0.isEmpty }
    }

    private func applyCurrentSnapshot(_ snapshot: CurrentSnapshot?) {
        guard let snapshot else {
            switchFeedbackMessage = "실패: 상태 확인"
            matchedProfile = nil
            authMatch = "unknown"
            return
        }
        activeLabel = snapshot.activeLabel
        matchedProfile = snapshot.matchedProfile
        authMatch = snapshot.authMatch
        // Only clear the message this probe itself plants; the aggregate
        // "실패: 일부 갱신" must survive until ALL sub-checks succeed.
        if switchFeedbackMessage == "실패: 상태 확인" {
            switchFeedbackMessage = nil
        }
    }

    // A transient 3am hiccup used to plant a permanent red failure row that no
    // amount of later successful refreshes would clear.
    private func clearTransientFailureMessage() {
        if let message = switchFeedbackMessage, transientFailureMessages.contains(message) {
            switchFeedbackMessage = nil
        }
    }

    private func refreshCurrentAsync(completion: ((Bool) -> Void)? = nil) {
        DispatchQueue.global(qos: .userInitiated).async {
            let snapshot = self.loadCurrentSnapshot()
            DispatchQueue.main.async {
                self.applyCurrentSnapshot(snapshot)
                self.rebuildMenu()
                completion?(snapshot != nil)
            }
        }
    }

    private func rebuildMenu() {
        menu.removeAllItems()
        statusItem.button?.title = menuBarTitle()

        addSwitchHeader()
        if profiles.isEmpty {
            addFeedbackRow("프로필 없음")
        }
        let activeLabelIsReliable = authMatch != "mismatch" && matchedProfile == nil
        for profile in profiles {
            let item = NSMenuItem(title: profile, action: #selector(switchProfile(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = profile
            item.isEnabled = !isSwitching
            if profile == matchedProfile || (activeLabelIsReliable && profile == activeLabel) {
                item.state = .on
            }
            menu.addItem(item)
        }
        if let switchFeedbackMessage {
            addFeedbackRow(switchFeedbackMessage)
        } else if authMatch == "mismatch", let matchedProfile {
            addFeedbackRow("active 라벨 갱신 필요: \(matchedProfile)")
        } else if authMatch == "mismatch" {
            addFeedbackRow("현재 계정: 저장 프로필과 불일치")
        }
        menu.addItem(.separator())

        addUsageHeader()
        addUsageColumnHeader()
        addUsageRows()
        menu.addItem(.separator())

        addClaudeUsageHeader()
        addClaudeUsageRows()

        menu.addItem(.separator())
        let quitItem = NSMenuItem(title: "종료", action: #selector(quit(_:)), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)
    }

    private func scheduleUsageRefreshTimer() {
        usageRefreshTimer?.invalidate()
        usageRefreshTimer = Timer.scheduledTimer(withTimeInterval: secondsUntilNextUsageRefreshSlot(), repeats: false) { [weak self] _ in
            self?.refreshAllData(showStatus: false)
            self?.scheduleUsageRefreshTimer()
        }
        if let usageRefreshTimer {
            RunLoop.main.add(usageRefreshTimer, forMode: .common)
        }
    }

    private func secondsUntilNextUsageRefreshSlot(from date: Date = Date()) -> TimeInterval {
        let calendar = Calendar.current
        let components = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        let minute = components.minute ?? 0
        let nextMinute = ((minute / usageRefreshMinuteStep) + 1) * usageRefreshMinuteStep

        var target = components
        if nextMinute >= 60 {
            guard let nextHour = calendar.date(byAdding: .hour, value: 1, to: calendar.date(from: components) ?? date) else {
                return TimeInterval(usageRefreshMinuteStep * 60)
            }
            target = calendar.dateComponents([.year, .month, .day, .hour], from: nextHour)
            target.minute = 0
        } else {
            target.minute = nextMinute
        }
        target.second = 0
        target.nanosecond = 0

        guard let targetDate = calendar.date(from: target) else {
            return TimeInterval(usageRefreshMinuteStep * 60)
        }
        return max(1, targetDate.timeIntervalSince(date))
    }

    private func menuBarTitle() -> String {
        guard let row = activeUsageRow(), let five = row["five_hour_left"] as? Int else {
            return isRefreshingUsage ? "◉ …%" : "◉ --%"
        }
        return "◉ \(five)%"
    }

    private func activeUsageRow() -> [String: Any]? {
        let activeFallback = (authMatch != "mismatch" && profiles.contains(activeLabel)) ? activeLabel : nil
        let preferredProfiles = [matchedProfile, activeFallback].compactMap { $0 }
        for profile in preferredProfiles {
            if let row = usageRows.first(where: { $0["profile"] as? String == profile }) {
                return row
            }
        }
        return nil
    }

    private func addSwitchHeader() {
        let refreshingAny = isRefreshingStatus || isRefreshingUsage || isRefreshingClaudeUsage
        let marker = refreshingAny ? "…" : "↻"
        let refreshAt = lastUsageRefresh ?? lastClaudeUsageRefresh

        let view = NSView(frame: NSRect(x: 0, y: 0, width: menuWidth, height: rowHeight))

        let label = NSTextField(labelWithString: "계정전환")
        label.frame = NSRect(x: 16, y: 3, width: menuWidth - 140, height: 18)
        label.lineBreakMode = .byTruncatingTail
        label.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        label.textColor = NSColor.labelColor
        view.addSubview(label)

        if let refreshAt {
            let meta = NSTextField(labelWithString: "갱신 \(refreshAt)")
            meta.frame = NSRect(x: menuWidth - 116, y: 3, width: 76, height: 18)
            meta.alignment = .right
            meta.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
            meta.textColor = NSColor.labelColor
            view.addSubview(meta)
        }

        let markerLabel = NSTextField(labelWithString: marker)
        markerLabel.frame = NSRect(x: menuWidth - 34, y: 3, width: 24, height: 18)
        markerLabel.alignment = .center
        markerLabel.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        markerLabel.textColor = refreshingAny ? NSColor.secondaryLabelColor : NSColor.controlAccentColor
        view.addSubview(markerLabel)

        let fullRowButton = NSButton(title: "", target: self, action: #selector(refreshStatus(_:)))
        fullRowButton.isBordered = false
        fullRowButton.isTransparent = true
        fullRowButton.frame = view.bounds
        fullRowButton.autoresizingMask = [.width, .height]
        fullRowButton.isEnabled = !refreshingAny
        view.addSubview(fullRowButton)

        let item = NSMenuItem()
        item.view = view
        menu.addItem(item)
    }

    private func addUsageHeader() {
        addSectionHeader("Codex 사용량", trailing: codexWeekResetText().map { "초기화 - \($0)" })
    }

    private func addClaudeUsageHeader() {
        let staleText = claudeUsageIsStale ? " · stale" : ""
        addSectionHeader("Claude 사용량\(staleText)", trailing: claudeResetSummaryText())
    }

    private func addSectionHeader(_ title: String, trailing: String?) {
        let item = NSMenuItem()
        let view = NSView(frame: NSRect(x: 0, y: 0, width: menuWidth, height: rowHeight))

        let label = NSTextField(labelWithString: title)
        label.frame = NSRect(x: 16, y: 3, width: 92, height: 18)
        label.lineBreakMode = .byTruncatingTail
        label.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        label.textColor = NSColor.labelColor
        view.addSubview(label)

        if let trailing, !trailing.isEmpty {
            let meta = NSTextField(labelWithString: trailing)
            meta.frame = NSRect(x: 104, y: 3, width: menuWidth - 120, height: 18)
            meta.alignment = .right
            meta.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
            meta.textColor = NSColor.labelColor
            view.addSubview(meta)
        }

        item.view = view
        menu.addItem(item)
    }

    private func addUsageColumnHeader() {
        addUsageRow(profile: "Profile", five: "5H", weekly: "Week", isHeader: true)
    }

    private func addUsageRows() {
        let byProfile = Dictionary(uniqueKeysWithValues: usageRows.compactMap { row -> (String, [String: Any])? in
            guard let profile = row["profile"] as? String else { return nil }
            return (profile, row)
        })
        for profile in profiles {
            let row = byProfile[profile]
            let five = row?["five_hour_left"] as? Int
            let weekly = row?["weekly_left"] as? Int
            let weeklyText = weekly.map { "\($0)%" } ?? "-"
            let fiveText = five.map { "\($0)%" } ?? "-"
            addUsageRow(
                profile: profile,
                five: fiveText,
                weekly: weeklyText,
                isHeader: false,
                fiveWarn: shouldWarnCodexRemaining(five),
                weeklyWarn: shouldWarnCodexRemaining(weekly)
            )
        }
    }

    private func addUsageRow(profile: String, five: String, weekly: String, isHeader: Bool, fiveWarn: Bool = false, weeklyWarn: Bool = false) {
        let item = NSMenuItem()
        let view = NSView(frame: NSRect(x: 0, y: 0, width: menuWidth, height: rowHeight))

        let profileLabel = NSTextField(labelWithString: profile)
        profileLabel.frame = NSRect(x: 16, y: 2, width: 146, height: 20)
        profileLabel.font = isHeader ? NSFont.boldSystemFont(ofSize: NSFont.systemFontSize) : NSFont.monospacedSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
        profileLabel.textColor = NSColor.labelColor
        view.addSubview(profileLabel)

        let fiveLabel = NSTextField(labelWithString: five)
        fiveLabel.frame = NSRect(x: 174, y: 2, width: 42, height: 20)
        fiveLabel.alignment = .right
        fiveLabel.font = isHeader ? NSFont.boldSystemFont(ofSize: NSFont.systemFontSize) : NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
        fiveLabel.textColor = NSColor.labelColor
        view.addSubview(fiveLabel)

        let fiveWarnLabel = NSTextField(labelWithString: (!isHeader && fiveWarn) ? "⚠" : "")
        fiveWarnLabel.frame = NSRect(x: 218, y: 2, width: 14, height: 20)
        fiveWarnLabel.alignment = .center
        fiveWarnLabel.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        fiveWarnLabel.textColor = NSColor.systemOrange
        view.addSubview(fiveWarnLabel)

        let weeklyLabel = NSTextField(labelWithString: weekly)
        weeklyLabel.frame = NSRect(x: 228, y: 2, width: 48, height: 20)
        weeklyLabel.alignment = .right
        weeklyLabel.font = isHeader ? NSFont.boldSystemFont(ofSize: NSFont.systemFontSize) : NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
        weeklyLabel.textColor = NSColor.labelColor
        view.addSubview(weeklyLabel)

        let weeklyWarnLabel = NSTextField(labelWithString: (!isHeader && weeklyWarn) ? "⚠" : "")
        weeklyWarnLabel.frame = NSRect(x: 278, y: 2, width: 14, height: 20)
        weeklyWarnLabel.alignment = .center
        weeklyWarnLabel.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        weeklyWarnLabel.textColor = NSColor.systemOrange
        view.addSubview(weeklyWarnLabel)

        item.view = view
        menu.addItem(item)
    }

    private func addClaudeUsageRows() {
        if let message = claudeUsageMessage, claudeFiveText == nil, claudeWeekText == nil {
            addClaudeMessageRow(message)
            if message == "로그인 필요" {
                addClaudeLoginItem()
            }
            return
        }
        addUsageColumnHeader()
        addUsageRow(
            profile: "Claude",
            five: claudeFiveText ?? "-",
            weekly: claudeWeekText ?? "-",
            isHeader: false,
            fiveWarn: shouldWarnClaudeUtilization(claudeFiveUtilization),
            weeklyWarn: shouldWarnClaudeUtilization(claudeWeekUtilization)
        )
        if let message = claudeUsageMessage {
            addClaudeMessageRow(message)
        }
    }

    private func addClaudeMessageRow(_ message: String) {
        let item = NSMenuItem()
        let view = NSView(frame: NSRect(x: 0, y: 0, width: menuWidth, height: rowHeight))
        let label = NSTextField(labelWithString: "Claude: \(message)")
        label.frame = NSRect(x: 16, y: 2, width: menuWidth - 32, height: 20)
        label.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        label.textColor = NSColor.labelColor
        view.addSubview(label)
        item.view = view
        menu.addItem(item)
    }

    private func addFeedbackRow(_ message: String) {
        let item = NSMenuItem()
        let view = NSView(frame: NSRect(x: 0, y: 0, width: menuWidth, height: rowHeight))
        let label = NSTextField(labelWithString: message)
        label.frame = NSRect(x: 16, y: 2, width: menuWidth - 32, height: 20)
        label.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        label.textColor = message.hasPrefix("실패") ? NSColor.systemRed : NSColor.secondaryLabelColor
        view.addSubview(label)
        item.view = view
        menu.addItem(item)
    }

    private func addClaudeLoginItem() {
        let item = NSMenuItem(title: "Claude 로그인", action: #selector(startClaudeLogin(_:)), keyEquivalent: "")
        item.target = self
        menu.addItem(item)
    }

    private func refreshUsageData(showStatus: Bool, completion: ((Bool) -> Void)? = nil) {
        if isRefreshingUsage {
            completion?(true)
            return
        }
        isRefreshingUsage = true
        rebuildMenu()
        DispatchQueue.global(qos: .userInitiated).async {
            let result = runSwitcher(["usage", "--json"])
            var newRows: [[String: Any]] = []
            var success = false
            if result.code == 0, let json = parseJSON(result.stdout), let rows = json["usage"] as? [[String: Any]] {
                newRows = rows
                success = true
            }
            DispatchQueue.main.async {
                if !newRows.isEmpty { self.usageRows = newRows }
                if success { self.lastUsageRefresh = nowTimeString() }
                if showStatus && !self.isRefreshingStatus && !success {
                    self.switchFeedbackMessage = "실패: usage 갱신"
                }
                self.isRefreshingUsage = false
                self.rebuildMenu()
                completion?(success)
            }
        }
    }

    private func refreshClaudeUsageData(showStatus: Bool, completion: ((Bool) -> Void)? = nil) {
        if isRefreshingClaudeUsage {
            completion?(true)
            return
        }
        isRefreshingClaudeUsage = true
        rebuildMenu()
        DispatchQueue.global(qos: .userInitiated).async {
            let result = runSwitcher(["claude-usage", "--json"])
            var success = false
            var five: String?
            var week: String?
            var fiveUtilization: Double?
            var weekUtilization: Double?
            var fiveReset: String?
            var weekReset: String?
            var message: String? = "조회 실패"
            if let json = parseJSON(result.stdout) {
                let authenticated = json["authenticated"] as? Bool ?? false
                if authenticated {
                    five = self.claudePercent(json: json, key: "five_hour")
                    week = self.claudePercent(json: json, key: "seven_day")
                    fiveUtilization = self.claudeUtilization(json: json, key: "five_hour")
                    weekUtilization = self.claudeUtilization(json: json, key: "seven_day")
                    fiveReset = self.claudeResetTimeText(json: json, key: "five_hour")
                    weekReset = self.claudeResetDateText(json: json, key: "seven_day")
                    message = (five == nil && week == nil) ? "표시값 없음" : nil
                    success = message == nil
                } else {
                    let error = json["error"] as? String
                    if error == "rate_limited" {
                        let retryAfter = self.numberValue(json["retry_after_seconds"]).map { Int($0) } ?? 0
                        let minutes = max(1, (retryAfter + 59) / 60)
                        message = "잠시 후 재시도 (\(minutes)분)"
                    } else {
                        message = "로그인 필요"
                    }
                }
            }
            DispatchQueue.main.async {
                if success {
                    self.claudeFiveText = five
                    self.claudeWeekText = week
                    self.claudeFiveUtilization = fiveUtilization
                    self.claudeWeekUtilization = weekUtilization
                    self.claudeFiveResetText = fiveReset
                    self.claudeWeekResetText = weekReset
                    self.lastClaudeUsageRefresh = nowTimeString()
                    self.claudeUsageIsStale = false
                    self.claudeUsageMessage = nil
                } else {
                    let hasPreviousValue = self.claudeFiveText != nil || self.claudeWeekText != nil
                    self.claudeUsageIsStale = hasPreviousValue
                    self.claudeUsageMessage = message
                    if !hasPreviousValue {
                        self.claudeFiveText = nil
                        self.claudeWeekText = nil
                        self.claudeFiveUtilization = nil
                        self.claudeWeekUtilization = nil
                        self.claudeFiveResetText = fiveReset
                        self.claudeWeekResetText = weekReset
                    }
                }
                if showStatus && !self.isRefreshingStatus && !success {
                    self.switchFeedbackMessage = "실패: Claude usage"
                }
                self.isRefreshingClaudeUsage = false
                self.rebuildMenu()
                completion?(success)
            }
        }
    }

    private func claudePercent(json: [String: Any], key: String) -> String? {
        guard let bucket = json[key] as? [String: Any], let utilization = numberValue(bucket["utilization"]) else { return nil }
        return percentText(utilization)
    }

    private func claudeUtilization(json: [String: Any], key: String) -> Double? {
        guard let bucket = json[key] as? [String: Any] else { return nil }
        return numberValue(bucket["utilization"])
    }

    private func shouldWarnCodexRemaining(_ value: Int?) -> Bool {
        guard let value else { return false }
        return value <= 20
    }

    private func shouldWarnClaudeUtilization(_ value: Double?) -> Bool {
        guard let value else { return false }
        return value >= 80
    }

    private func claudeResetDateText(json: [String: Any], key: String) -> String? {
        guard let bucket = json[key] as? [String: Any] else { return nil }
        return resetDateText(bucket["resets_at"] as? String)
    }

    private func claudeResetTimeText(json: [String: Any], key: String) -> String? {
        guard let bucket = json[key] as? [String: Any] else { return nil }
        return resetTimeText(bucket["resets_at"] as? String)
    }

    private func claudeResetSummaryText() -> String? {
        switch (claudeFiveResetText, claudeWeekResetText) {
        case let (five?, week?):
            return "초기화 - \(five) / \(week)"
        case let (five?, nil):
            return "초기화 - \(five)"
        case let (nil, week?):
            return "초기화 - \(week)"
        default:
            return nil
        }
    }

    private func codexWeekResetText() -> String? {
        guard let reset = activeUsageRow()?["weekly_reset"] as? String else { return nil }
        return resetDateText(reset)
    }

    private func resetDateText(_ raw: String?) -> String? {
        return resetText(raw, format: "MM.dd (E)")
    }

    private func resetTimeText(_ raw: String?) -> String? {
        return resetText(raw, format: "HH:mm")
    }

    private func resetText(_ raw: String?, format: String) -> String? {
        guard let raw, !raw.isEmpty else { return nil }
        let formatterWithFraction = ISO8601DateFormatter()
        formatterWithFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let formatterPlain = ISO8601DateFormatter()
        formatterPlain.formatOptions = [.withInternetDateTime]
        guard let date = formatterWithFraction.date(from: raw) ?? formatterPlain.date(from: raw) else { return nil }
        let output = DateFormatter()
        output.locale = Locale(identifier: "ko_KR")
        output.dateFormat = format
        output.timeZone = .current
        return output.string(from: date)
    }

    private func numberValue(_ value: Any?) -> Double? {
        if let number = value as? Double { return number }
        if let number = value as? Int { return Double(number) }
        if let number = value as? NSNumber { return number.doubleValue }
        return nil
    }

    private func percentText(_ value: Double) -> String {
        if value.rounded() == value { return "\(Int(value))%" }
        return String(format: "%.1f%%", value)
    }

    @objc private func refreshStatus(_ sender: Any) {
        refreshAllData(showStatus: true)
    }

    @objc private func startClaudeLogin(_ sender: NSMenuItem) {
        switchFeedbackMessage = nil
        rebuildMenu()
        guard let scriptURL = writeClaudeLoginCommandFile() else {
            switchFeedbackMessage = "실패: Claude 로그인"
            rebuildMenu()
            return
        }
        NSWorkspace.shared.open(scriptURL)
        rebuildMenu()
    }

    private func writeClaudeLoginCommandFile() -> URL? {
        guard let switcherPath else { return nil }
        let tmpDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex-switch/tmp", isDirectory: true)
        let url = tmpDir.appendingPathComponent("codex-claude-login-\(UUID().uuidString).command")
        let script = """
        #!/bin/zsh
        trap 'rm -f "$0"' EXIT
        clear
        echo "Claude 로그인"
        echo "브라우저가 열리면 Claude OAuth code만 복사해서 아래에 붙여넣으세요."
        echo
        "\(switcherPath)" claude-login
        login_status=$?
        echo
        if [ "$login_status" = "0" ]; then
          echo "로그인 완료. 메뉴바에서 계정전환 행을 눌러 새로고침하세요."
        else
          echo "로그인 실패. 위 에러 메시지를 확인하세요."
          echo "429 rate limit이면 같은 코드를 계속 재시도하지 말고, 잠시 뒤 새 로그인으로 다시 시도하세요."
        fi
        echo
        echo "Enter를 누르면 창을 닫습니다."
        read
        """
        do {
            try FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: tmpDir.path)
            try script.write(to: url, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
            return url
        } catch {
            return nil
        }
    }

    private func refreshAllData(showStatus: Bool) {
        if isRefreshingStatus { return }
        isRefreshingStatus = true
        if showStatus { switchFeedbackMessage = nil }
        rebuildMenu()

        let group = DispatchGroup()
        var currentOK = false
        var usageOK = false
        var claudeOK = false

        group.enter()
        // loadProfiles may launch the backend; keep it off the main thread.
        DispatchQueue.global(qos: .userInitiated).async {
            let loaded = self.loadProfiles()
            DispatchQueue.main.async {
                self.profiles = loaded
                group.leave()
            }
        }

        group.enter()
        refreshCurrentAsync { ok in
            currentOK = ok
            group.leave()
        }

        group.enter()
        refreshUsageData(showStatus: false) { ok in
            usageOK = ok
            group.leave()
        }

        group.enter()
        refreshClaudeUsageData(showStatus: false) { ok in
            claudeOK = ok
            group.leave()
        }

        group.notify(queue: .main) {
            self.isRefreshingStatus = false
            let allOK = currentOK && usageOK && claudeOK
            if showStatus {
                self.switchFeedbackMessage = allOK ? nil : "실패: 일부 갱신"
            } else if allOK {
                self.clearTransientFailureMessage()
            }
            self.rebuildMenu()
        }
    }

    private func requestCodexQuit() {
        let apps = NSRunningApplication.runningApplications(withBundleIdentifier: "com.openai.codex")
        for app in apps {
            app.terminate()
        }
    }

    private func waitForCodexToStop(timeoutSeconds: TimeInterval) -> (stopped: Bool, hint: String) {
        let deadline = Date().addingTimeInterval(timeoutSeconds)
        var lastJSON: [String: Any]?
        repeat {
            // Cheap in-process check first; the backend launch (which can also see
            // app-server and tool sessions) only runs once the bundle apps are gone.
            if NSRunningApplication.runningApplications(withBundleIdentifier: "com.openai.codex").isEmpty {
                let result = runSwitcher(["current", "--json"])
                if result.code == 0, let json = parseJSON(result.stdout) {
                    lastJSON = json
                    if let running = json["codex_running"] as? Bool, running == false {
                        return (true, "")
                    }
                }
            }
            Thread.sleep(forTimeInterval: 0.5)
        } while Date() < deadline
        return (false, processHint(from: lastJSON))
    }

    @objc private func switchProfile(_ sender: NSMenuItem) {
        guard let profile = sender.representedObject as? String else { return }
        guard !isSwitching else { return }
        isSwitching = true
        switchFeedbackMessage = "\(profile) 전환 중"
        rebuildMenu()
        DispatchQueue.global(qos: .userInitiated).async {
            self.requestCodexQuit()
            var stopState = self.waitForCodexToStop(timeoutSeconds: 12)
            var cleanupHint: String?
            if !stopState.stopped {
                let cleanup = runSwitcher(["stop-codex", "--json", "--grace-seconds", "4"])
                cleanupHint = processHint(from: parseJSON(cleanup.stdout))
                stopState = self.waitForCodexToStop(timeoutSeconds: 8)
            }
            guard stopState.stopped else {
                _ = runSwitcher(["open"])
                DispatchQueue.main.async {
                    self.isSwitching = false
                    self.switchFeedbackMessage = "실패: \(cleanupHint ?? stopState.hint)"
                    self.refreshCurrentAsync()
                }
                return
            }

            let result = runSwitcher(["use", profile, "--apply", "--json"])
            let openAfterSuccess = result.code == 0
            let message: String
            if result.code == 0 {
                _ = runSwitcher(["open"])
                message = "\(profile) 전환 완료"
            } else {
                _ = runSwitcher(["open"])
                message = switchFailureMessage(result: result)
            }
            DispatchQueue.main.async {
                self.isSwitching = false
                self.switchFeedbackMessage = message
                self.switchFeedbackGeneration += 1
                let generation = self.switchFeedbackGeneration
                if openAfterSuccess {
                    self.activeLabel = profile
                    self.authMatch = "matched"
                    self.matchedProfile = profile
                    // Success confirmation shows briefly, then clears itself. The
                    // generation check stops a stale timer from erasing a NEWER
                    // switch's identical message.
                    DispatchQueue.main.asyncAfter(deadline: .now() + 8) {
                        if self.switchFeedbackGeneration == generation, self.switchFeedbackMessage == message {
                            self.switchFeedbackMessage = nil
                            self.rebuildMenu()
                        }
                    }
                }
                self.refreshCurrentAsync()
            }
        }
    }

    @objc private func quit(_ sender: NSMenuItem) {
        NSApp.terminate(nil)
    }
}

let app = NSApplication.shared
let delegate = CodexMenuApp()
app.delegate = delegate
app.run()
