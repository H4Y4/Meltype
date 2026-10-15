// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

import Cocoa
import InputMethodKit

if CommandLine.arguments.contains("--version") {
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
    print("Meltype \(version)")
    exit(0)
}

if CommandLine.arguments.contains("--register-input-source") {
    exit(InputSourceRegistration.register() == 0 ? 0 : 1)
}

// Meltype --export-settings <ファイル> / --import-settings <ファイル>: 設定を書き出す・読み込む
// (Windows の設定画面の「書き出す...」と同じ形。読み込んだ設定は新しいプロファイルになり、今の設定はプロファイルとして残る)
for option in ["--export-settings", "--import-settings"] {
    guard let index = CommandLine.arguments.firstIndex(of: option) else { continue }
    guard index + 1 < CommandLine.arguments.count else {
        FileHandle.standardError.write("使い方: Meltype \(option) <ファイル>\n".data(using: .utf8)!)
        exit(2)
    }
    let path = URL(fileURLWithPath: CommandLine.arguments[index + 1]).path
    let importing = option == "--import-settings"
    let result = importing ? NativeCore.shared.importSettings(from: path) : NativeCore.shared.exportSettings(to: path)
    if result.ok && importing {
        // 動いている Meltype に、入力の本体を作り直して新しい設定を読むように知らせる
        DistributedNotificationCenter.default().postNotificationName(
            Notification.Name("MeltypeSettingsChanged"), object: nil, userInfo: nil, deliverImmediately: true)
    }
    print(result.message)
    exit(result.ok ? 0 : 1)
}

// Input Method Kit のサーバーを起動する。入力欄 (クライアント) ごとに MeltypeInputController が作られる。

/// 変換の候補の一覧 (すべての入力欄で共有する)。
var candidatesWindow: IMKCandidates?

// 別の Meltype が IME として動いていれば、終わるまで待つ (ServerLock の説明を参照)。
// 待っている間は本体も読み込まない (学習データを 2 つの Meltype で書き換えないように)。
var serverLock: Int32?
if !CommandLine.arguments.contains("--self-test") && !CommandLine.arguments.contains("--check-inputs"),
   let bundleIdentifier = Bundle.main.bundleIdentifier {
    let url = ServerLock.defaultURL(bundleIdentifier: bundleIdentifier)
    serverLock = ServerLock.acquire(at: url, wait: false)
    if serverLock == nil {
        NSLog("Meltype: 別の Meltype が動いているので、終わるまで待ちます")
        serverLock = ServerLock.acquire(at: url, wait: true)
        NSLog("Meltype: 前の Meltype が終わったので、引き継ぎます")
    }
}

// 本体 (libMeltypeNative.dylib) を読み込み、漢字変換・英単語の判定の関数を登録しておく。
NativeCore.shared.initialize()

if CommandLine.arguments.contains("--self-test") || CommandLine.arguments.contains("--check-inputs") {
    var failures = 0
    func check(_ name: String, _ condition: Bool) {
        print("\(condition ? "PASS" : "FAIL") \(name)")
        if !condition { failures += 1 }
    }
    /// field を渡すと、実際の入力欄と同じように前後の文字を本体へ渡す: 変換中の文字が無いキーのときだけ、
    /// キャレットの前の文字 (field.before + ここまでに確定した文字) と後ろの文字 (field.after) を渡す (InputController.handle と同じ)。
    func type(_ text: String, session: UnsafeMutableRawPointer?, before: String? = nil, field: (before: String, after: String)? = nil) -> (String, SessionResult?) {
        var output = ""
        var last: SessionResult?
        for character in text {
            var before = before
            var after: String?
            if let field, last?.view == nil { (before, after) = (field.before + output, field.after) }
            let vk: Int32
            switch character {
            case " ": vk = 0x20
            case "\n": vk = 0x0D
            case "\t": vk = 0x09
            default: vk = character.lowercased().unicodeScalars.first.flatMap(KeyMapping.virtualKey(forCharacter:)) ?? 0
            }
            let code = Int32(String(character).utf16.first ?? 0)
            last = NativeCore.shared.handleKey(session, vk: vk, character: code,
                modifiers: character.isUppercase ? 1 : 0, before: before, after: after)
            for edit in last?.commits ?? [] {
                if edit.deleteBefore > 0 {
                    let current = output as NSString
                    output = current.substring(to: max(0, current.length - edit.deleteBefore))
                }
                output += edit.text
            }
            if last?.consumed != true { output += String(character) }
        }
        return (output, last)
    }
    if let index = CommandLine.arguments.firstIndex(of: "--check-inputs"), index + 1 < CommandLine.arguments.count {
        do {
            let content = try String(contentsOfFile: CommandLine.arguments[index + 1], encoding: .utf8)
            var count = 0
            for line in content.split(separator: "\n") where !line.hasPrefix("#") {
                // 入力<TAB>期待する出力[<TAB>入力欄のキャレットの前の文字[<TAB>後ろの文字]]。前後の文字の中の \\n は改行。
                let parts = line.split(separator: "\t", maxSplits: 3, omittingEmptySubsequences: false)
                guard parts.count >= 2 else { continue }
                let text = { (index: Int) in parts.count > index ? parts[index].replacingOccurrences(of: "\\n", with: "\n") : "" }
                let field = parts.count > 2 ? (before: text(2), after: text(3)) : nil
                let session = NativeCore.shared.createSession()
                let (output, _) = type(String(parts[0]) + "\n", session: session, field: field)
                NativeCore.shared.destroySession(session)
                check(String(parts[0]), output == String(parts[1]))
                if output != String(parts[1]) { print("expected: \(parts[1]); actual: \(output)") }
                count += 1
            }
            print("\(count - failures)/\(count) input checks passed")
            exit(count > 0 && failures == 0 ? 0 : 1)
        } catch {
            print("Input check file could not be read: \(error.localizedDescription)")
            exit(1)
        }
    }
    for (input, expected) in [("kyouhagoogledekensaku \n", "今日はgoogleで検索"),
                               ("I want to go to the park\n", "I want to go to the park"),
                               ("google github hello\n", "google github hello"),
                               ("seeyouagain\n", "see you again")] {
        let session = NativeCore.shared.createSession()
        let (output, _) = type(input, session: session)
        print("result: \(output)")
        check(input.trimmingCharacters(in: .whitespacesAndNewlines), output == expected)
        NativeCore.shared.destroySession(session)
    }
    let englishSession = NativeCore.shared.createSession()
    let (reflected, _) = type("reflectsareta\n", session: englishSession)
    check("issue 77: reflect + Japanese", reflected == "reflectされた")
    NativeCore.shared.destroySession(englishSession)
    let sentence = "kyouhagoogledekensakusitekekkawomiru\n"
    let started = Date()
    let checksBefore = NativeCore.shared.spellCheckCount
    let firstSession = NativeCore.shared.createSession()
    let (firstOutput, _) = type(sentence, session: firstSession)
    NativeCore.shared.destroySession(firstSession)
    let checksAfterFirst = NativeCore.shared.spellCheckCount
    let secondSession = NativeCore.shared.createSession()
    let (secondOutput, _) = type(sentence, session: secondSession)
    NativeCore.shared.destroySession(secondSession)
    let repeatedChecks = NativeCore.shared.spellCheckCount - checksAfterFirst
    print("issue 114: first checks=\(checksAfterFirst - checksBefore), repeat checks=\(repeatedChecks), elapsed=\(Date().timeIntervalSince(started))s")
    check("issue 114: repeated spelling uses cache", repeatedChecks == 0 && firstOutput == secondOutput)
    let other: [String: Any] = ["InputSourceKind": "Keyboard Layout", "KeyboardLayout Name": "ABC"]
    let duplicate: [String: Any] = ["Bundle ID": InputSourceRegistration.identifier, "InputSourceKind": "Keyboard Input Method"]
    let repaired = InputSourceRegistration.normalized([other] + Array(repeating: duplicate, count: 15))
    check("issue 101: deduplicate input sources", repaired.count == 3 && (repaired[0]["KeyboardLayout Name"] as? String) == "ABC")
    check("issue 101: registration is idempotent", NSArray(array: repaired).isEqual(to: InputSourceRegistration.normalized(repaired)))
    do {
        for format in [PropertyListSerialization.PropertyListFormat.xml, .binary] {
            let data = try PropertyListSerialization.data(fromPropertyList: [["shortcut": "カイシャ", "phrase": "株式会社テスト"]], format: format, options: 0)
            let entries = try MacUserDictionary.entries(from: data)
            let (merged, added) = MacUserDictionary.merge(entries + entries, into: "# existing\nなまえ\t既存の名前\n")
            check("issue 40: plist import \(format)", added == 1 && merged.contains("かいしゃ\t株式会社テスト") && merged.contains("なまえ\t既存の名前"))
            check("issue 40: reimport is idempotent", MacUserDictionary.merge(entries, into: merged).1 == 0)
        }
    } catch { check("issue 40: plist parsing", false) }
    let session = NativeCore.shared.createSession()
    let (_, goPreview) = type("go", session: session)
    check("go preview candidates", goPreview?.view?.candidates == ["go", "ご"])
    let selectedGo = NativeCore.shared.selectCandidate(session, index: 0)
    check("select English go", selectedGo?.view?.text == "go")
    _ = NativeCore.shared.commit(session)
    _ = type("go", session: session)
    let selectedKana = NativeCore.shared.selectCandidate(session, index: 1)
    check("select hiragana go", selectedKana?.view?.text == "ご")
    _ = NativeCore.shared.commit(session)
    let (_, emoji) = type("egao ", session: session)
    check("emoji candidate", emoji?.view?.candidates.contains("😊") == true)
    _ = NativeCore.shared.commit(session)
    let (_, typo) = type("buresureddo", session: session)
    check("misspelling suggestion", typo?.view?.suggestion?.contains("ブレスレット") == true)
    let (_, corrected) = type("\t", session: session)
    print("corrected: \(corrected?.view?.text ?? "nil")")
    check("misspelling correction", ["ぶれすれっと", "ブレスレット"].contains(corrected?.view?.text ?? ""))
    _ = NativeCore.shared.commit(session)
    NativeCore.shared.setCodeInput(session, true)
    let (code, _) = type("kyouha", session: session, before: "let value = ")
    check("code stays ASCII", code == "kyouha")
    let (_, comment) = type("kyouha", session: session, before: "// ")
    check("comment composes Japanese", comment?.view?.text.contains("きょう") == true || comment?.view?.text.contains("今日") == true)
    NativeCore.shared.destroySession(session)
    let stressSession = NativeCore.shared.createSession()
    let stressStart = Date()
    for iteration in 0..<1000 {
        _ = type("kyouhagoogledekensaku ", session: stressSession)
        _ = NativeCore.shared.selectCandidate(stressSession, index: iteration % 2)
        let committed = NativeCore.shared.commit(stressSession)
        check("stress commit \(iteration)", committed?.view == nil)
        NativeCore.shared.setDirect(stressSession, true)
        _ = type("hello", session: stressSession)
        NativeCore.shared.setDirect(stressSession, false)
        let (output, _) = type("wsldeshell\n", session: stressSession)
        check("stress mode switch \(iteration)", output == "wslでshell")
    }
    NativeCore.shared.destroySession(stressSession)
    print("1000 native input/candidate/commit/mode cycles in \(Date().timeIntervalSince(stressStart))s")
    exit(failures == 0 ? 0 : 1)
}

let connectionName = Bundle.main.object(forInfoDictionaryKey: "InputMethodConnectionName") as? String ?? "Meltype_Connection"
guard let bundleIdentifier = Bundle.main.bundleIdentifier,
      let server = IMKServer(name: connectionName, bundleIdentifier: bundleIdentifier) else {
    NSLog("Meltype: IMKServer を起動できませんでした (Meltype.app から起動してください)")
    exit(1)
}
candidatesWindow = IMKCandidates(server: server, panelType: kIMKSingleColumnScrollingCandidatePanel)
// ニューラル変換のモデルの読み込みは、起動が済んでから (最初の 1 文字で待たせないため)。
DispatchQueue.main.async { MeltypeConverter.shared.warmUp() }
NSRegisterServicesProvider(ModeServices.shared, ModeServices.portName)
NSApplication.shared.run()
