// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

import Carbon.HIToolbox
import Cocoa
import InputMethodKit

/// 入力欄 (クライアント) ごとの IME。キーを本体 (libMeltypeNative.dylib) に渡し、
/// 返ってきた結果 (確定する文字・変換中の表示・候補) を入力欄に反映する。
/// Info.plist の InputMethodServerControllerClass に書いた名前で、Input Method Kit が作る。
@objc(MeltypeInputController)
final class MeltypeInputController: IMKInputController {
    private var session: UnsafeMutableRawPointer?
    private var candidateList: [String] = []
    private var hasMarkedText = false
    private var codeInput = false
    private var directInput = false
    private var suggestionPanel: NSPanel?
    private var displayedSuggestion: String?
    private var dictionaryObserver: NSObjectProtocol?

    override func activateServer(_ sender: Any!) {
        super.activateServer(sender)
        NativeCore.shared.setCodeInput(session, codeInput)
    }

    override init!(server: IMKServer!, delegate: Any!, client inputClient: Any!) {
        super.init(server: server, delegate: delegate, client: inputClient)
        session = NativeCore.shared.createSession()
        dictionaryObserver = DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name("MeltypeUserDictionaryChanged"), object: nil, queue: .main
        ) { [weak self] _ in
            guard let self else { return }
            self.commitComposition(self.client())
            NativeCore.shared.destroySession(self.session)
            self.session = NativeCore.shared.createSession()
            NativeCore.shared.setDirect(self.session, self.directInput)
            NativeCore.shared.setCodeInput(self.session, self.codeInput)
        }
    }

    deinit {
        if let dictionaryObserver { DistributedNotificationCenter.default().removeObserver(dictionaryObserver) }
        NativeCore.shared.destroySession(session)
    }

    override func recognizedEvents(_ sender: Any!) -> Int {
        Int(NSEvent.EventTypeMask.keyDown.rawValue)
    }

    override func handle(_ event: NSEvent!, client sender: Any!) -> Bool {
        guard let event, event.type == .keyDown, let client = sender as? IMKTextInput else { return false }
        if event.keyCode == kVK_Space, event.characters == "　" || event.characters == "\u{00A0}",
           event.modifierFlags.intersection([.command, .control]).isEmpty {
            apply(NativeCore.shared.commit(session), to: client)
            client.insertText(NSAttributedString(string: " "), replacementRange: NSRange(location: NSNotFound, length: 0))
            return true
        }

        // JIS キーボードの「英数」「かな」キー: 英数 (直接入力) ⇔ 日本語。
        switch Int(event.keyCode) {
        case kVK_JIS_Eisu:
            switchToDirectInput(client)
            return true
        case kVK_JIS_Kana:
            switchToJapanese()
            return true
        default:
            break
        }

        // US 配列など「英数」「かな」キーが無いキーボード向けに、macOS 標準と同じ Control+Shift+J (日本語) /
        // Control+Shift+; ・ ' (英数) でも切り替える。Command・Option が一緒のときは対象外。
        // 文字は Shift で変わる (US の Shift+; は ":") ので、物理キーの位置 (keyCode) で判定する。
        // JIS 配列の「;」キーは ANSI の ; と、「:」キーは ANSI の ' と同じ位置なので、同じ keyCode で拾える。
        // Dvorak など配列が違うときも、刻印ではなく QWERTY 上の同じ位置のキーで反応する。
        if event.modifierFlags.contains([.control, .shift]), event.modifierFlags.intersection([.command, .option]).isEmpty {
            switch Int(event.keyCode) {
            case kVK_ANSI_J:
                switchToJapanese()
                return true
            case kVK_ANSI_Semicolon, kVK_ANSI_Quote:
                // 変換中は、まず普通のキーとして本体へ渡す。Ctrl キーの割り当て (Mac 式の Ctrl+: = 半角英字に変換など) が
                // 本体にあればそちらを先にする。こうすると、割り当てがあるかどうかを Swift 側が設定ファイルまで見て調べなくて済む。
                // 割り当てが無いときの本体は、未確定の内容を確定して「アプリへ通す」(consumed=false) と返すので、
                // そのキーはアプリへ通さず、確定はもう済んでいるのでもう一度 commit せずに英数へ切り替える。
                if hasMarkedText, let vk = KeyMapping.virtualKey(for: event),
                   let result = sendToCore(event, vk: vk, client: client) {
                    apply(result, to: client)
                    if result.consumed { return true }
                    directInput = true
                    NativeCore.shared.setDirect(session, true)
                    return true
                }
                switchToDirectInput(client)
                return true
            default:
                break
            }
        }

        guard let vk = KeyMapping.virtualKey(for: event) else { return false }
        // event.characters は「見た目の 1 文字」で、UTF-16 要素が複数のことがある
        // (補助面の文字・結合文字・ZWJ 絵文字・異体字セレクター)。1 要素へ切り詰めたり 0 に置き換えたりしない。
        // 1 スカラーならそのコードポイントのまま本体へ渡し、複数スカラーは未確定内容だけ確定して
        // 元のイベントを 1 回アプリへ通す (本体側の pass-through 契約と対にする)。
        if (event.characters ?? "").unicodeScalars.count > 1 {
            apply(NativeCore.shared.commitBeforeExternalText(session, text: event.characters ?? ""), to: client)
            return false
        }
        guard let result = sendToCore(event, vk: vk, client: client) else { return false }
        apply(result, to: client)
        return result.consumed
    }

    /// キーを本体へ渡して結果を返す (入力欄への反映は呼び出し側が apply で行う)。
    /// character は event.characters の 1 スカラー (無ければ 0)。modifiers は Shift=1・Control=2・Option=4・Command=8。
    private func sendToCore(_ event: NSEvent, vk: Int32, client: IMKTextInput) -> SessionResult? {
        let scalars = Array((event.characters ?? "").unicodeScalars)
        // 矢印・Delete・Home/End・PageUp/PageDown・F1〜F35 などは、characters に私用領域の文字
        // (NSUpArrowFunctionKey U+F700 〜 NSModeSwitchFunctionKey U+F747) が入る。文字ではないので、
        // 文字なし (0) として vk だけを渡す。本体側 (Exports.cs の IsMacFunctionKeyScalar) と同じ範囲。
        // その外の私用領域 (U+F8FF の Apple ロゴなど) は、ふつうの文字として渡す。
        let functionKeyRange: ClosedRange<UInt32> = 0xF700...0xF747
        var character: Int32 = scalars.count == 1 && !functionKeyRange.contains(scalars[0].value) ? Int32(scalars[0].value) : 0
        let flags = event.modifierFlags
        // Control を押していると characters は Shift を無視する (US 配列の Ctrl+: が ";" で届く)。
        // Mac 式の Ctrl+; / Ctrl+: / Ctrl+' は文字で見分けるので、この 3 つだけ Shift を含めた文字を渡す。
        if flags.contains(.control), let base = event.charactersIgnoringModifiers?.unicodeScalars, base.count == 1,
           [";", ":", "'"].contains(base.first!) {
            character = Int32(base.first!.value)
        }
        var modifiers: Int32 = 0
        if flags.contains(.shift) { modifiers |= 1 }
        if flags.contains(.control) { modifiers |= 2 }
        if flags.contains(.option) { modifiers |= 4 }
        if flags.contains(.command) { modifiers |= 8 }

        let (before, after) = hasMarkedText ? (nil, nil) : surroundingText(of: client)
        return NativeCore.shared.handleKey(session, vk: vk, character: character, modifiers: modifiers, before: before, after: after)
    }

    /// フォーカスが外れた・クリックで別の場所に移ったときなど。未確定の内容をそのまま確定する。
    override func commitComposition(_ sender: Any!) {
        guard let client = (sender as? IMKTextInput) ?? self.client() else { return }
        apply(NativeCore.shared.commit(session), to: client)
    }

    override func deactivateServer(_ sender: Any!) {
        commitComposition(sender)
        candidatesWindow?.hide()
        suggestionPanel?.orderOut(nil)
        displayedSuggestion = nil
        meaningKey = nil
        candidateList = []
        super.deactivateServer(sender)
    }

    // ---- 変換の候補の一覧 ----

    override func candidates(_ sender: Any!) -> [Any]! {
        candidateList
    }

    override func candidateSelected(_ candidateString: NSAttributedString!) {
        guard let client = self.client(),
              let string = candidateString?.string,
              let index = candidateList.firstIndex(of: string) else { return }
        // クリックした候補が候補ウィンドウで選ばれているので、覚えている位置も合わせる。
        windowIndex = index
        apply(NativeCore.shared.selectCandidate(session, index: index), to: client)
    }

    /// 候補ウィンドウの中で選択が動いた (マウスで選んだときなど)。本体の選択も合わせる。
    /// 本体の選択を候補ウィンドウに反映しているとき (selectingFromCore) にも呼ばれるので、そのときは何もしない。
    /// moveDown / moveUp の通知が遅れて届くと古い番号に戻してしまうので、マウスの操作のときだけ受け付ける。
    private var selectingFromCore = false

    override func candidateSelectionChanged(_ candidateString: NSAttributedString!) {
        let mouseTypes: [NSEvent.EventType] = [.leftMouseDown, .leftMouseUp, .leftMouseDragged]
        guard !selectingFromCore,
              let type = NSApp.currentEvent?.type, mouseTypes.contains(type),
              let client = self.client(),
              let string = candidateString?.string,
              let index = candidateList.firstIndex(of: string) else { return }
        windowIndex = index
        apply(NativeCore.shared.selectCandidate(session, index: index), to: client)
    }

    // ---- メニュー (メニューバーの入力メニュー) ----

    override func menu() -> NSMenu! {
        let menu = NSMenu()
        for (title, action, selected) in [
            ("日本語・英語の自動判定", #selector(selectAutomatic(_:)), !codeInput && !directInput),
            ("英数の直接入力", #selector(selectDirect(_:)), directInput),
            ("コード入力 (コメント・文字列は日本語)", #selector(selectCode(_:)), codeInput && !directInput)
        ] {
            let item = menu.addItem(withTitle: title, action: action, keyEquivalent: "")
            item.target = self
            item.state = selected ? .on : .off
        }
        menu.addItem(.separator())
        let importItem = menu.addItem(withTitle: "macOSのユーザー辞書を取り込む…", action: #selector(importUserDictionary(_:)), keyEquivalent: "")
        importItem.target = self
        menu.addItem(withTitle: "Meltype のデータフォルダを開く (設定・ユーザー辞書)", action: #selector(openDataFolder(_:)), keyEquivalent: "")
        menu.addItem(withTitle: "不具合の報告・提案… (Mac 版はプレビュー版です)", action: #selector(openReport(_:)), keyEquivalent: "")
        return menu
    }

    private func selectMode(direct: Bool, code: Bool) {
        commitComposition(client())
        directInput = direct
        codeInput = code
        NativeCore.shared.setDirect(session, direct)
        NativeCore.shared.setCodeInput(session, code)
    }

    /// 「英数」キー・Control+Shift+; ・ ': 未確定を確定して、英数 (直接入力) にする。
    private func switchToDirectInput(_ client: IMKTextInput) {
        apply(NativeCore.shared.commit(session), to: client)
        directInput = true
        NativeCore.shared.setDirect(session, true)
    }

    /// 「かな」キー・Control+Shift+J: 日本語 (自動判定) に戻す。
    private func switchToJapanese() {
        directInput = false
        codeInput = false
        NativeCore.shared.setCodeInput(session, false)
        NativeCore.shared.setDirect(session, false)
    }

    @objc private func selectAutomatic(_ sender: Any?) { selectMode(direct: false, code: false) }
    @objc private func selectDirect(_ sender: Any?) { selectMode(direct: true, code: false) }
    @objc private func selectCode(_ sender: Any?) { selectMode(direct: false, code: true) }

    @objc private func importUserDictionary(_ sender: Any?) {
        commitComposition(client())
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let source = panel.url,
              let directory = NativeCore.shared.dataDirectory else { return }
        let alert = NSAlert()
        do {
            let count = try MacUserDictionary.importFile(source, directory: directory)
            alert.messageText = "ユーザー辞書に\(count)語を追加しました"
            DistributedNotificationCenter.default().postNotificationName(
                Notification.Name("MeltypeUserDictionaryChanged"), object: nil, userInfo: nil, deliverImmediately: true)
        } catch {
            alert.messageText = "ユーザー辞書を取り込めませんでした"
            alert.informativeText = error.localizedDescription
        }
        alert.runModal()
    }

    @objc private func openReport(_ sender: Any?) {
        guard let url = NativeCore.shared.reportUrl else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func openDataFolder(_ sender: Any?) {
        guard let directory = NativeCore.shared.dataDirectory else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: directory, isDirectory: true))
    }

    // ---- 結果を入力欄に反映する ----

    private func apply(_ result: SessionResult?, to client: IMKTextInput) {
        guard let result else { return }
        for edit in result.commits {
            var range = NSRange(location: NSNotFound, length: 0)
            if edit.deleteBefore > 0 {
                // 確定し直し: キャレット (変換中の文字があればその先頭) の前の文字を置き換える。
                let marked = client.markedRange()
                let caret = marked.location != NSNotFound && marked.length > 0 ? marked.location : client.selectedRange().location
                if caret != NSNotFound {
                    let length = min(edit.deleteBefore, caret)
                    range = NSRange(location: caret - length, length: length)
                }
            }
            client.insertText(NSAttributedString(string: edit.text), replacementRange: range)
            hasMarkedText = false
        }
        if let view = result.view {
            showComposition(view, client: client)
        } else {
            hideComposition(client: client)
        }
    }

    /// 変換中の文字を入力欄に下線付きで出す (変換中は文節ごと、選んでいる文節は太い下線)。
    private func showComposition(_ view: CompositionView, client: IMKTextInput) {
        let text = NSMutableAttributedString(string: view.text)
        let length = (view.text as NSString).length
        if view.converting && !view.clauses.isEmpty {
            var location = 0
            for (index, clause) in view.clauses.enumerated() {
                let clauseLength = (clause as NSString).length
                let style = index == view.selectedClause ? kTSMHiliteSelectedConvertedText : kTSMHiliteConvertedText
                addMark(style, to: text, range: NSRange(location: location, length: clauseLength))
                location += clauseLength
            }
        } else {
            addMark(kTSMHiliteRawText, to: text, range: NSRange(location: 0, length: length))
        }
        client.setMarkedText(NSAttributedString(attributedString: text), selectionRange: NSRange(location: length, length: 0), replacementRange: NSRange(location: NSNotFound, length: 0))
        hasMarkedText = length > 0
        updateCandidates(view)
        showSuggestion(view.suggestion, client: client)
    }

    private func hideComposition(client: IMKTextInput) {
        if hasMarkedText {
            // Keep the payload type consistent when clearing marked text.
            client.setMarkedText(NSAttributedString(string: ""), selectionRange: NSRange(location: 0, length: 0), replacementRange: NSRange(location: NSNotFound, length: 0))
            hasMarkedText = false
        }
        candidateList = []
        candidatesWindow?.hide()
        suggestionPanel?.orderOut(nil)
        displayedSuggestion = nil
        meaningKey = nil
    }

    private func showSuggestion(_ suggestion: String?, client: IMKTextInput) {
        let selection = client.selectedRange()
        guard let suggestion, selection.location != NSNotFound else {
            suggestionPanel?.orderOut(nil)
            displayedSuggestion = nil
            return
        }
        var caret = NSRect.zero
        _ = client.attributes(forCharacterIndex: selection.location, lineHeightRectangle: &caret)
        guard caret.minX.isFinite, caret.minY.isFinite,
              caret.width.isFinite, caret.height.isFinite, caret.height > 0 else {
            suggestionPanel?.orderOut(nil)
            displayedSuggestion = nil
            return
        }
        displayedSuggestion = suggestion
        let label = NSTextField(labelWithString: suggestion)
        label.font = .systemFont(ofSize: 13)
        label.sizeToFit()
        let size = NSSize(width: label.frame.width + 24, height: label.frame.height + 16)
        let panel = suggestionPanel ?? NSPanel(contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating
        panel.hasShadow = true
        panel.backgroundColor = .windowBackgroundColor
        panel.ignoresMouseEvents = true
        panel.setContentSize(size)
        panel.contentView?.subviews.forEach { $0.removeFromSuperview() }
        label.frame.origin = NSPoint(x: 12, y: 8)
        panel.contentView?.addSubview(label)
        if let screen = NSScreen.screens.first(where: { $0.frame.intersects(caret) }) ?? NSScreen.main {
            let visible = screen.visibleFrame
            panel.setFrameOrigin(NSPoint(x: max(visible.minX, min(caret.minX, visible.maxX - size.width)),
                y: max(visible.minY, min(caret.minY - size.height - 6, visible.maxY - size.height))))
        }
        suggestionPanel = panel
        panel.orderFrontRegardless()
    }

    private func addMark(_ style: Int, to text: NSMutableAttributedString, range: NSRange) {
        guard range.length > 0, range.location + range.length <= text.length,
              let marks = mark(forStyle: style, at: range) else { return }
        var attributes: [NSAttributedString.Key: Any] = [:]
        for (key, value) in marks {
            if let name = key as? String {
                attributes[NSAttributedString.Key(name)] = value
            } else if let name = key as? NSAttributedString.Key {
                attributes[name] = value
            }
        }
        text.addAttributes(attributes, range: range)
    }

    /// 候補で少し (1.5 秒) 止まったら、その候補の意味を候補ウィンドウの注釈に出す (Windows 版と同じ)。
    private var meaningKey: String?

    private func scheduleMeaning(_ view: CompositionView) {
        let key = view.meaning.map { "\(view.selectedIndex):\($0)" }
        guard key != meaningKey else { return }
        meaningKey = key
        guard let key, let meaning = view.meaning else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            guard let self, self.meaningKey == key, let window = candidatesWindow, window.isVisible() else { return }
            window.showAnnotation(NSAttributedString(string: meaning))
        }
    }

    private func updateCandidates(_ view: CompositionView) {
        guard let window = candidatesWindow else { return }
        if view.candidates.count > 1 {
            // 作り直し・選択で候補ウィンドウから candidateSelectionChanged が来ても、本体に返さない。
            selectingFromCore = true
            defer { selectingFromCore = false }
            // 一覧を作り直すと選択が先頭に戻るので、中身が変わったとき (別の文節に移ったときなど) だけ作り直す。
            if candidateList != view.candidates || !window.isVisible() {
                candidateList = view.candidates
                window.update()
                window.show(kIMKLocateCandidatesBelowHint)
                windowIndex = 0
            }
            if view.selectedIndex >= 0 && view.selectedIndex < view.candidates.count {
                selectInWindow(window, index: view.selectedIndex)
            }
            scheduleMeaning(view)
        } else {
            meaningKey = nil
            candidateList = []
            window.hide()
        }
    }

    /// 候補ウィンドウで今選ばれている候補の番号 (一覧を作り直すと先頭に戻る)。
    private var windowIndex = 0

    /// 候補ウィンドウの選択を index 番目の候補に合わせる。
    /// selectCandidate(withIdentifier:) では選択が動かず、selectedCandidate() も今の選択とずれることがある (macOS 26)。
    /// なので、選択の位置はこちらで覚えておき、矢印キーと同じ moveDown / moveUp でその差だけ動かす。
    private func selectInWindow(_ window: IMKCandidates, index: Int) {
        let step = index > windowIndex ? #selector(NSResponder.moveDown(_:)) : #selector(NSResponder.moveUp(_:))
        guard window.responds(to: step) else { return }
        for _ in 0..<abs(index - windowIndex) {
            window.perform(step, with: nil)
        }
        windowIndex = index
    }

    /// 入力欄のキャレットの前後の文字列 (それぞれ 20 文字まで)。取れなければ nil。
    private func surroundingText(of client: IMKTextInput) -> (String?, String?) {
        let selection = client.selectedRange()
        guard selection.location != NSNotFound else { return (nil, nil) }
        let start = max(0, selection.location - (codeInput ? 4000 : 20))
        let before = client.attributedSubstring(from: NSRange(location: start, length: selection.location - start))?.string
        let after = client.attributedSubstring(from: NSRange(location: selection.location + selection.length, length: 20))?.string
        return (before, after)
    }
}
