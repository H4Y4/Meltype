// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

import AppKit
import Foundation

// libMeltypeNative.dylib (C# の Meltype.Core を NativeAOT にしたもの) の関数。src/Meltype.Mac.Native/Exports.cs と合わせる。
typealias ClausesCallback = @convention(c) (UnsafePointer<CChar>?, UnsafePointer<CChar>?, UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
typealias CandidatesCallback = @convention(c) (UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
typealias IsWordCallback = @convention(c) (UnsafePointer<CChar>?) -> Int32

private typealias InitFunction = @convention(c) (ClausesCallback, CandidatesCallback, IsWordCallback) -> Int32
private typealias CreateFunction = @convention(c) () -> UnsafeMutableRawPointer?
private typealias DestroyFunction = @convention(c) (UnsafeMutableRawPointer?) -> Void
private typealias HandleKeyFunction = @convention(c) (UnsafeMutableRawPointer?, Int32, Int32, Int32, UnsafePointer<CChar>?, UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
private typealias CommitFunction = @convention(c) (UnsafeMutableRawPointer?) -> UnsafeMutablePointer<CChar>?
private typealias SelectFunction = @convention(c) (UnsafeMutableRawPointer?, Int32) -> UnsafeMutablePointer<CChar>?
private typealias ReconvertFunction = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?, UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
private typealias SetDirectFunction = @convention(c) (UnsafeMutableRawPointer?, Int32) -> Void
private typealias DataDirectoryFunction = @convention(c) () -> UnsafeMutablePointer<CChar>?
private typealias SessionStringFunction = @convention(c) (UnsafeMutableRawPointer?) -> UnsafeMutablePointer<CChar>?
private typealias ReportUrlFunction = @convention(c) (UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
private typealias FreeFunction = @convention(c) (UnsafeMutableRawPointer?) -> Void

// ---- 本体から呼ばれる関数 (文字列は strdup したものを返し、本体が free する) ----

/// (ひらがな, 前の文脈, 後ろの文脈) → 「読み\t変換結果」を改行でつないだもの (文節ごと)。
private let clausesCallback: ClausesCallback = { hiragana, context, following in
    guard let hiragana else { return nil }
    let clauses = MeltypeConverter.shared.clauses(for: String(cString: hiragana), context: context.map { String(cString: $0) },
                                                  following: following.map { String(cString: $0) })
    guard !clauses.isEmpty else { return nil }
    return strdup(clauses.map { "\($0.reading)\t\($0.text)" }.joined(separator: "\n"))
}

/// 読み → 候補を改行でつないだもの。
private let candidatesCallback: CandidatesCallback = { reading in
    guard let reading else { return nil }
    let candidates = MeltypeConverter.shared.candidates(for: String(cString: reading))
    return strdup(candidates.joined(separator: "\n"))
}

/// 英単語として正しい綴りか (macOS のスペルチェッカー、英語で調べる)。
private let spellCacheLock = NSLock()
nonisolated(unsafe) private var spellCache: [String: Bool] = [:]
nonisolated(unsafe) private var spellCacheExpiration = Date.distantPast
nonisolated(unsafe) private var spellChecks = 0
private let isWordCallback: IsWordCallback = { word in
    guard let word else { return 0 }
    let text = String(cString: word)
    // macOS のスペルチェッカーは、73 文字以上の文字列をどれも「正しい綴り」と返す (意味のない英字の並びでも)。
    // 長い文を続けて打つと、文全体が英単語と判定されて英字のまま確定されていた。それほど長い英単語は無いので、英単語ではないと返す。
    if text.utf8.count > 45 { return 0 }
    let cached: Bool? = spellCacheLock.withLock {
        if Date() >= spellCacheExpiration {
            spellCache.removeAll(keepingCapacity: true)
            spellCacheExpiration = Date().addingTimeInterval(300)
        }
        return spellCache[text]
    }
    if let cached { return cached ? 1 : 0 }
    let misspelled = NSSpellChecker.shared.checkSpelling(of: text, startingAt: 0, language: "en", wrap: false, inSpellDocumentWithTag: 0, wordCount: nil)
    let valid = misspelled.location == NSNotFound
    spellCacheLock.withLock {
        spellChecks += 1
        if spellCache.count >= 4096 { spellCache.removeAll(keepingCapacity: true) }
        spellCache[text] = valid
    }
    return valid ? 1 : 0
}

/// Swift 側で扱う結果 (本体の SessionResult.ToJson と同じ形)。
struct SessionResult: Decodable {
    let consumed: Bool
    let commits: [TextEdit]
    let view: CompositionView?
}

struct TextEdit: Decodable {
    let deleteBefore: Int
    let text: String
}

struct CompositionView: Decodable {
    let text: String
    let converting: Bool
    let selectedIndex: Int
    let selectedClause: Int
    let hint: String
    let candidates: [String]
    let clauses: [String]
    /// 選んでいる候補の意味 (無ければ nil)。候補で少し止まったら注釈に出す。
    let meaning: String?
    let suggestion: String?
}

/// libMeltypeNative.dylib を読み込んで呼ぶ。Meltype.app/Contents/Frameworks に置く (build.sh)。
final class NativeCore {
    static let shared = NativeCore()
    var spellCheckCount: Int { spellCacheLock.withLock { spellChecks } }

    private let library: UnsafeMutableRawPointer?
    private let initFunction: InitFunction?
    private let createFunction: CreateFunction?
    private let destroyFunction: DestroyFunction?
    private let handleKeyFunction: HandleKeyFunction?
    private let commitFunction: CommitFunction?
    private let selectFunction: SelectFunction?
    private let reconvertFunction: ReconvertFunction?
    private let setDirectFunction: SetDirectFunction?
    private let setCodeInputFunction: SetDirectFunction?
    private let latinKeyboardLayoutFunction: SessionStringFunction?
    private let dataDirectoryFunction: DataDirectoryFunction?
    private let reportUrlFunction: ReportUrlFunction?
    private let exportSettingsFunction: ReportUrlFunction?
    private let importSettingsFunction: ReportUrlFunction?
    private let freeFunction: FreeFunction?

    private init() {
        let path = (Bundle.main.privateFrameworksPath ?? "") + "/libMeltypeNative.dylib"
        // 初期化が終わるまで self のプロパティは使えないので、ローカルの handle から関数を探す。
        let handle = dlopen(path, RTLD_NOW)
        library = handle
        if handle == nil, let error = dlerror() {
            NSLog("Meltype: %@ を読み込めませんでした: %@", path, String(cString: error))
        }
        func symbol<T>(_ name: String, as type: T.Type) -> T? {
            guard let handle, let pointer = dlsym(handle, name) else { return nil }
            return unsafeBitCast(pointer, to: type)
        }
        initFunction = symbol("meltype_init", as: InitFunction.self)
        createFunction = symbol("meltype_create", as: CreateFunction.self)
        destroyFunction = symbol("meltype_destroy", as: DestroyFunction.self)
        handleKeyFunction = symbol("meltype_handle_key", as: HandleKeyFunction.self)
        commitFunction = symbol("meltype_commit", as: CommitFunction.self)
        selectFunction = symbol("meltype_select_candidate", as: SelectFunction.self)
        reconvertFunction = symbol("meltype_reconvert", as: ReconvertFunction.self)
        setDirectFunction = symbol("meltype_set_direct", as: SetDirectFunction.self)
        setCodeInputFunction = symbol("meltype_set_code_input", as: SetDirectFunction.self)
        latinKeyboardLayoutFunction = symbol("meltype_latin_keyboard_layout", as: SessionStringFunction.self)
        dataDirectoryFunction = symbol("meltype_data_directory", as: DataDirectoryFunction.self)
        reportUrlFunction = symbol("meltype_report_url", as: ReportUrlFunction.self)
        exportSettingsFunction = symbol("meltype_export_settings", as: ReportUrlFunction.self)
        importSettingsFunction = symbol("meltype_import_settings", as: ReportUrlFunction.self)
        freeFunction = symbol("meltype_free", as: FreeFunction.self)
    }

    func initialize() {
        _ = initFunction?(clausesCallback, candidatesCallback, isWordCallback)
    }

    func createSession() -> UnsafeMutableRawPointer? { createFunction?() }

    func destroySession(_ session: UnsafeMutableRawPointer?) { destroyFunction?(session) }

    func handleKey(_ session: UnsafeMutableRawPointer?, vk: Int32, character: Int32, modifiers: Int32, before: String?, after: String?) -> SessionResult? {
        guard let handleKeyFunction else { return nil }
        return withOptionalCString(before) { beforePointer in
            withOptionalCString(after) { afterPointer in
                decode(handleKeyFunction(session, vk, character, modifiers, beforePointer, afterPointer))
            }
        }
    }

    func commit(_ session: UnsafeMutableRawPointer?) -> SessionResult? {
        guard let commitFunction else { return nil }
        return decode(commitFunction(session))
    }

    /// 元の複数スカラーのイベントをアプリに渡す前に、未確定の内容だけ確定する。
    /// 結合文字から始まるイベントは、1 スカラーの場合と同じく直前の文字との境界を保つ。
    func commitBeforeExternalText(_ session: UnsafeMutableRawPointer?, text: String) -> SessionResult? {
        if let first = text.unicodeScalars.first {
            switch first.properties.generalCategory {
            case .nonspacingMark, .spacingMark, .enclosingMark:
                return handleKey(session, vk: 0x07, character: Int32(first.value), modifiers: 0, before: nil, after: nil)
            default:
                break
            }
        }
        return commit(session)
    }

    func selectCandidate(_ session: UnsafeMutableRawPointer?, index: Int) -> SessionResult? {
        guard let selectFunction else { return nil }
        return decode(selectFunction(session, Int32(index)))
    }

    /// 選択した文字 (text) とそのひらがなの読み (reading) で再変換を始める。始められなければ consumed が false の結果。
    func reconvert(_ session: UnsafeMutableRawPointer?, text: String, reading: String) -> SessionResult? {
        guard let reconvertFunction else { return nil }
        return text.withCString { textPointer in
            reading.withCString { readingPointer in
                decode(reconvertFunction(session, textPointer, readingPointer))
            }
        }
    }

    func setDirect(_ session: UnsafeMutableRawPointer?, _ direct: Bool) {
        setDirectFunction?(session, direct ? 1 : 0)
    }

    func setCodeInput(_ session: UnsafeMutableRawPointer?, _ enabled: Bool) {
        setCodeInputFunction?(session, enabled ? 1 : 0)
    }

    /// 設定「英字のレイアウト」(config.json の LatinKeyboardLayout)。指定が無ければ nil。
    func latinKeyboardLayout(_ session: UnsafeMutableRawPointer?) -> String? {
        guard let pointer = latinKeyboardLayoutFunction?(session) else { return nil }
        defer { freeFunction?(pointer) }
        let layout = String(cString: pointer)
        return layout.isEmpty ? nil : layout
    }

    /// 設定・学習データ・ユーザー辞書の保存場所。
    var dataDirectory: String? {
        guard let pointer = dataDirectoryFunction?() else { return nil }
        defer { freeFunction?(pointer) }
        return String(cString: pointer)
    }

    /// 不具合報告を開く URL (OS・版・実行環境を入れたもの)。
    var reportUrl: URL? {
        guard let pointer = "Mac".withCString({ reportUrlFunction?($0) }) else { return nil }
        defer { freeFunction?(pointer) }
        return URL(string: String(cString: pointer))
    }

    /// 今の設定を path に書き出す (Windows の設定画面の「書き出す...」と同じ形)。ok と、人に見せる文。
    func exportSettings(to path: String) -> (ok: Bool, message: String) {
        transferSettings(exportSettingsFunction, path)
    }

    /// path の設定を新しいプロファイルとして読み込んで使う (今の設定はプロファイルとして残る)。ok と、人に見せる文。
    func importSettings(from path: String) -> (ok: Bool, message: String) {
        transferSettings(importSettingsFunction, path)
    }

    /// 結果は 1 行目が "ok" か "error"、2 行目が人に見せる文 (Exports.cs の meltype_export_settings)。
    private func transferSettings(_ function: ReportUrlFunction?, _ path: String) -> (ok: Bool, message: String) {
        guard let function else { return (false, "この版の本体 (libMeltypeNative.dylib) は、設定の書き出し・読み込みに対応していません。") }
        guard let pointer = path.withCString({ function($0) }) else { return (false, "結果を受け取れませんでした。") }
        defer { freeFunction?(pointer) }
        let lines = String(cString: pointer).split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false)
        return (lines.first == "ok", lines.count > 1 ? String(lines[1]) : "")
    }

    private func decode(_ pointer: UnsafeMutablePointer<CChar>?) -> SessionResult? {
        guard let pointer else { return nil }
        defer { freeFunction?(pointer) }
        let json = Data(bytes: pointer, count: strlen(pointer))
        do {
            return try JSONDecoder().decode(SessionResult.self, from: json)
        } catch {
            NSLog("Meltype: 結果を読めませんでした: %@", String(describing: error))
            return nil
        }
    }

    private func withOptionalCString<R>(_ text: String?, _ body: (UnsafePointer<CChar>?) -> R) -> R {
        guard let text else { return body(nil) }
        return text.withCString { body($0) }
    }
}
