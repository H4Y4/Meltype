// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

import Foundation
import KanaKanjiConverterModuleWithDefaultDictionary

/// azooKey の変換エンジン (AzooKeyKanaKanjiConverter) で、ひらがなを漢字かな混じりに変換する。
/// azooKey の API が変わったときは、このファイルだけ直せばよいようにしてある。
final class MeltypeConverter {
    static let shared = MeltypeConverter()

    // 同梱の辞書 (KanaKanjiConverterModuleWithDefaultDictionary) を使う変換エンジン。
    // azooKey の withDefaultDictionary() は辞書を Bundle.module で探すが、Bundle.module は Meltype.app の直下か
    // ビルドしたマシンのフォルダーしか見ない。辞書のバンドルは Contents/Resources に入れているので、見つからずに落ちていた (#26)。
    // (Meltype.app の直下には置けない: 署名が通らない)。なので、辞書の場所をこちらで渡す。
    private let converter = KanaKanjiConverter(dicdataStore: DicdataStore(dictionaryURL: MeltypeConverter.resource("Dictionary")))
    private let directory: URL
    /// ニューラル変換 (Zenzai) のモデル。同梱していない・設定で止めているときは nil (辞書と統計だけで変換する)。
    private let zenzaiWeight: URL?
    private var resultCache: [String: [Candidate]] = [:]
    private var cacheOrder: [String] = []

    /// 同梱の辞書のバンドル (Contents/Resources に入れている) の中のフォルダー。
    private static func resource(_ name: String) -> URL {
        let bundleName = "AzooKeyKanaKanjiConverter_KanaKanjiConverterModuleWithDefaultDictionary.bundle"
        let url = (Bundle.main.resourceURL ?? Bundle.main.bundleURL).appendingPathComponent(bundleName, isDirectory: true)
        return (Bundle(url: url)?.resourceURL ?? url).appendingPathComponent(name, isDirectory: true)
    }

    /// 絵文字の辞書 (azooKey の withDefaultEmojiDictionary() と同じ選び方。こちらも Bundle.module を使わない)。
    private static func emojiDictionary() -> URL {
        let directory = resource("EmojiDictionary")
        if #available(macOS 15.3, *) { return directory.appendingPathComponent("emoji_all_E16.0.txt", isDirectory: false) }
        if #available(macOS 14.4, *) { return directory.appendingPathComponent("emoji_all_E15.1.txt", isDirectory: false) }
        return directory.appendingPathComponent("emoji_all_E15.0.txt", isDirectory: false)
    }

    /// ニューラル変換の推論の回数の上限。1 回だと速いが精度が落ち、5 回より増やしてもほとんど変わらない
    /// (AJIMEE-Bench の 1 位の正解率: 使わない 44% / 1 回 67% / 3 回 84% / 5 回 86% / 10 回 86%)。
    private static let zenzaiInferenceLimit = 5

    /// 同梱のニューラル変換のモデル (build.sh が Contents/Resources/zenz に入れる)。
    /// `defaults write io.github.yksr-melt.inputmethod.Meltype DisableNeuralConversion -bool true` で止められる (Meltype を起動し直すと反映)。
    private static func bundledZenzaiWeight() -> URL? {
        if UserDefaults.standard.bool(forKey: "DisableNeuralConversion") { return nil }
        let directory = (Bundle.main.resourceURL ?? Bundle.main.bundleURL).appendingPathComponent("zenz", isDirectory: true)
        let files = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "gguf" }.sorted { $0.lastPathComponent < $1.lastPathComponent }.last
    }

    private init() {
        // azooKey の学習データ・ユーザー辞書の置き場所 (Meltype では学習しない設定にしているので、ほぼ使わない)。
        directory = (NativeCore.shared.dataDirectory.map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("Meltype", isDirectory: true))
            .appendingPathComponent("azooKey", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        zenzaiWeight = MeltypeConverter.bundledZenzaiWeight()
        NSLog("Meltype: ニューラル変換 (Zenzai) %@", zenzaiWeight.map { "を使います: \($0.lastPathComponent)" } ?? "は使いません")
    }

    /// 変換の設定。context を渡すと、ニューラル変換がキャレットの前の文字を文脈として使う (この本は + あつい → 厚い)。
    /// neural が false なら、辞書と統計だけで変換する (候補の一覧用: 速く、候補の種類が多い)。
    private func options(neural: Bool, context: String?) -> ConvertRequestOptions {
        var zenzai = ConvertRequestOptions.ZenzaiMode.off
        if neural, let zenzaiWeight {
            zenzai = .on(weight: zenzaiWeight, inferenceLimit: MeltypeConverter.zenzaiInferenceLimit, personalizationMode: nil,
                         versionDependentMode: .v3(.init(leftSideContext: context)))
        }
        return ConvertRequestOptions(
            requireJapanesePrediction: .disabled,
            requireEnglishPrediction: .disabled,
            keyboardLanguage: .ja_JP,
            learningType: .nothing,
            memoryDirectoryURL: directory,
            sharedContainerURL: directory,
            textReplacer: TextReplacer(emojiDataProvider: { MeltypeConverter.emojiDictionary() }),
            specialCandidateProviders: KanaKanjiConverter.defaultSpecialCandidateProviders,
            zenzaiMode: zenzai,
            metadata: .init(versionString: "Meltype \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown")")
        )
    }

    /// ひらがな全体に対する変換結果 (入力をすべて使ったものだけ、よい順)。
    private func results(for hiragana: String, neural: Bool, context: String? = nil) -> [Candidate] {
        // ニューラル変換を使わないときは、文脈で結果が変わらない。
        let neural = neural && zenzaiWeight != nil
        let key = neural ? "n\u{1}\(context ?? "")\u{1}\(hiragana)" : "c\u{1}\(hiragana)"
        if let cached = resultCache[key] { return cached }
        var composing = ComposingText()
        composing.insertAtCursorPosition(hiragana, inputStyle: .direct)
        let results = converter.requestCandidates(composing, options: options(neural: neural, context: context))
        let count = hiragana.count
        // 入力 (ひらがな) をすべて使った候補だけ (.direct で入れたので、入力の文字数 = ひらがなの文字数)
        let candidates = results.mainResults.filter { $0.composingCount == .inputCount(count) || $0.composingCount == .surfaceCount(count) }
        // Keep the engine's previous lattice for incremental conversion; bound retained results.
        if count <= 256 {
            if cacheOrder.count >= 64 {
                resultCache.removeValue(forKey: cacheOrder.removeFirst())
            }
            resultCache[key] = candidates
            cacheOrder.append(key)
        }
        return candidates
    }

    /// 文節に区切った変換結果 (読みをつなげると元のひらがなになる)。変換できなければ空。
    func clauses(for hiragana: String, context: String?) -> [(reading: String, text: String)] {
        guard let best = results(for: hiragana, neural: true, context: context).first else { return [] }
        var clauses: [(reading: String, text: String)] = []
        for element in best.data {
            let reading = element.ruby.applyingTransform(.hiraganaToKatakana, reverse: true) ?? element.ruby
            // 助詞・助動詞 (ひらがなだけの短い語) は前の文節にくっつける (今日|は → 今日は)。
            if let last = clauses.last, element.word == reading, element.word.count <= 3 {
                clauses[clauses.count - 1] = (last.reading + reading, last.text + element.word)
            } else {
                clauses.append((reading, element.word))
            }
        }
        // 読みがずれた (長音などの扱いの違い) ときは、文節に分けずに全体で返す。
        if clauses.map(\.reading).joined() != hiragana {
            return [(hiragana, best.text)]
        }
        return clauses
    }

    /// ニューラル変換のモデルを先に読み込んでおく (最初の 1 文字で待たせないため)。
    func warmUp() {
        guard zenzaiWeight != nil else { return }
        _ = clauses(for: "にほんご", context: nil)
        converter.stopComposition()
    }

    /// 読みに対する候補の一覧 (重複なし、30 個まで)。
    func candidates(for reading: String) -> [String] {
        var seen = Set<String>()
        var list: [String] = []
        for candidate in results(for: reading, neural: false) where seen.insert(candidate.text).inserted {
            list.append(candidate.text)
            if list.count >= 30 { break }
        }
        return list
    }
}
