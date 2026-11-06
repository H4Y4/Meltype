// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 H4Y4

import AppKit

/// 選んでいる候補の意味を、候補の一覧の横に出すパネル (macOS 標準の日本語入力が辞書の意味を出すのと同じ位置)。
/// 候補を選ぶとすぐ出し、選び直すたびに中身を入れ替える。候補の一覧はすべての入力欄で共有するので、このパネルも 1 つだけ。
final class MeaningPanel {
    static let shared = MeaningPanel()

    /// パネルの幅 (意味は 2 つまで、1 つは 1〜2 行程度)。
    static let width: CGFloat = 280
    /// 候補の一覧との間。
    static let gap: CGFloat = 4
    private static let padding: CGFloat = 10
    private static let fontSize: CGFloat = 13

    private var panel: NSPanel?
    private var label: NSTextField?

    private init() {}

    var isVisible: Bool { panel?.isVisible == true }

    /// candidates (候補の一覧の枠、画面の座標) の右に、上端をそろえて出す。右に入らなければ左に出す。
    func show(_ meaning: String, beside candidates: NSRect, level: NSWindow.Level) {
        guard !meaning.isEmpty, candidates.width > 0, candidates.height > 0,
              let screen = NSScreen.screens.first(where: { $0.frame.intersects(candidates) }) ?? NSScreen.main else {
            hide()
            return
        }
        let visible = screen.visibleFrame
        let (panel, label) = makePanel()
        label.stringValue = meaning
        // 文の高さに合わせる。候補の一覧より低ければ、候補の一覧と同じ高さにする (純正の辞書の表示と同じ並び)
        let textWidth = Self.width - Self.padding * 2
        let textHeight = ceil(label.cell?.cellSize(forBounds: NSRect(x: 0, y: 0, width: textWidth, height: .greatestFiniteMagnitude)).height ?? 0)
        let height = min(max(textHeight + Self.padding * 2, candidates.height), visible.height)
        var x = candidates.maxX + Self.gap
        if x + Self.width > visible.maxX { x = candidates.minX - Self.gap - Self.width }
        x = max(visible.minX, min(x, visible.maxX - Self.width))
        let y = max(visible.minY, min(candidates.maxY - height, visible.maxY - height))
        label.frame = NSRect(x: Self.padding, y: height - Self.padding - min(textHeight, height - Self.padding * 2),
                             width: textWidth, height: min(textHeight, height - Self.padding * 2))
        panel.level = level
        panel.setFrame(NSRect(x: x, y: y, width: Self.width, height: height), display: true)
        panel.contentView?.setAccessibilityLabel(meaning)
        panel.orderFrontRegardless()
    }

    func hide() {
        panel?.orderOut(nil)
    }

    private func makePanel() -> (NSPanel, NSTextField) {
        if let panel, let label { return (panel, label) }
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 60),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        // 打っている入力欄からフォーカスもクリックも奪わない
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        // 候補の一覧と同じ、OS のすりガラスの背景 (ライト / ダークに合わせる)
        let background = NSVisualEffectView()
        background.material = .menu
        background.blendingMode = .behindWindow
        background.state = .active
        background.wantsLayer = true
        background.layer?.cornerRadius = 8
        background.layer?.masksToBounds = true
        background.autoresizingMask = [.width, .height]
        background.setAccessibilityElement(true)
        background.setAccessibilityRole(.staticText)
        let label = NSTextField(wrappingLabelWithString: "")
        label.font = .systemFont(ofSize: Self.fontSize)
        label.textColor = .labelColor
        label.lineBreakMode = .byWordWrapping
        label.maximumNumberOfLines = 0
        label.isSelectable = false
        label.setAccessibilityElement(false)
        background.addSubview(label)
        panel.contentView = background
        self.panel = panel
        self.label = label
        return (panel, label)
    }
}
