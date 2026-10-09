// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 H4Y4

import Foundation

/// 選択した確定済みの文字を再変換する (Control+Shift+R) ための、読みの推定。
enum Reconversion {
    /// 再変換できる選択文字の長さの上限 (UTF-16 の要素数。本体の MeltypeSession.MaxReconversionLength と同じ)。
    static let maxLength = 128

    /// 取り消したり元の文字のまま確定したりすると、変換中の文字がプレーンテキストで入れ直されるので、書式が失われる選択か。
    /// リンク・添付、または範囲によって属性が違う (一部だけ太字など) ものは true。属性が全体で一様なら false。
    static func hasRichFormatting(_ text: NSAttributedString) -> Bool {
        guard text.length > 0 else { return false }
        let whole = NSRange(location: 0, length: text.length)
        var rich = false
        text.enumerateAttributes(in: whole, options: []) { attributes, _, stop in
            if attributes[.link] != nil || attributes[.attachment] != nil {
                rich = true
                stop.pointee = true
            }
        }
        if rich { return true }
        var range = NSRange(location: 0, length: 0)
        _ = text.attributes(at: 0, longestEffectiveRange: &range, in: whole)
        return range.length != text.length
    }

    /// 選択した文字のひらがなの読み。再変換できない文字 (英数字・記号・空白・改行を含む、長すぎる) や、読みが取れないときは nil。
    ///
    /// - かな・カタカナ: ひらがなにするだけ (長音 ー と中黒 ・ はそのまま)。
    /// - 漢字を含む語: CFStringTokenizer (ja) の語ごとのローマ字読みをひらがなにする。
    /// - 英数字・記号: 読みを決められないので再変換しない (変換結果を元に戻せなくなるのを避ける)。
    static func reading(of text: String) -> String? {
        guard !text.isEmpty, text.utf16.count <= maxLength,
              text.unicodeScalars.allSatisfy({ isJapanese($0) }) else { return nil }
        let string = text as NSString
        let tokenizer = CFStringTokenizerCreate(nil, text as CFString, CFRange(location: 0, length: string.length),
                                                kCFStringTokenizerUnitWordBoundary, CFLocaleCreate(nil, CFLocaleIdentifier("ja" as CFString)))
        var result = ""
        var cursor = 0
        while CFStringTokenizerAdvanceToNextToken(tokenizer) != [] {
            let range = CFStringTokenizerGetCurrentTokenRange(tokenizer)
            // 語と語の間 (語として切り出されなかった部分) はそのまま
            if range.location > cursor {
                result += hiragana(string.substring(with: NSRange(location: cursor, length: range.location - cursor)))
            }
            let token = string.substring(with: NSRange(location: range.location, length: range.length))
            if isKanaOrPunctuation(token) {
                result += hiragana(token)
            } else {
                guard let latin = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String,
                      let kana = latin.applyingTransform(.latinToHiragana, reverse: false) else { return nil }
                result += kana
            }
            cursor = range.location + range.length
        }
        if cursor < string.length {
            result += hiragana(string.substring(from: cursor))
        }
        return isReading(result) ? result : nil
    }

    /// かな・和文の記号・漢字か。英数字・空白・改行などは含めない (英単語は読みに直せず、ローマ字読みでかなになってしまうので)。
    private static func isJapanese(_ scalar: Unicode.Scalar) -> Bool {
        (0x3001...0x30FF).contains(scalar.value) || scalar.properties.isIdeographic
    }

    /// カタカナをひらがなにする (ひらがな・長音・記号はそのまま)。
    private static func hiragana(_ text: String) -> String {
        // ICU の Katakana-Hiragana は長音 ー を直前の母音 (お・い) に変えてしまうので、コードポイントをずらして変える。
        var result = String.UnicodeScalarView()
        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x30A1...0x30F6, 0x30FD, 0x30FE: result.append(Unicode.Scalar(scalar.value - 0x60)!)
            default: result.append(scalar)
            }
        }
        return String(result)
    }

    private static func isKanaOrPunctuation(_ text: String) -> Bool {
        text.unicodeScalars.allSatisfy { (0x3001...0x30FF).contains($0.value) }
    }

    /// ひらがな・長音・中黒・和文の句読点やかぎかっこだけか (空文字は不可)。
    private static func isReading(_ text: String) -> Bool {
        !text.isEmpty && text.unicodeScalars.allSatisfy {
            switch $0.value {
            case 0x3041...0x3096, 0x309D, 0x309E, 0x30FB, 0x30FC, 0x3001...0x303F: return true
            default: return false
            }
        }
    }
}
