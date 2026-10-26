// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

using System.Runtime.InteropServices;
using System.Text.Json;
using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;
using Meltype.Mac;

namespace Meltype.Tests;

/// <summary>C ABI の実際の入口を呼ぶ。辞書・学習データはスタブで、ユーザーの設定を読み書きしない。</summary>
internal static unsafe class NativeBoundaryTests
{
    private sealed class NativeSession : IDisposable
    {
        public MeltypeSession Session { get; } = new(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { SpaceAroundEnglish = () => true }, () => new Settings());
        private readonly GCHandle _handle;

        public NativeSession() => _handle = GCHandle.Alloc(Session);

        public JsonDocument Key(int scalar, int? vk = null, string? after = null)
        {
            delegate* unmanaged<IntPtr, int, int, int, byte*, byte*, byte*> handleKey = &Exports.HandleKey;
            var afterPointer = after is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(after);
            byte* pointer;
            try
            {
                pointer = handleKey(GCHandle.ToIntPtr(_handle), vk ??
                    (scalar is >= 'a' and <= 'z' ? scalar - 0x20 : 0x07), scalar, 0, null, (byte*)afterPointer);
            }
            finally { Marshal.FreeCoTaskMem(afterPointer); }
            Assert.True(pointer != null, "C ABI が有効な JSON を返す");
            try { return JsonDocument.Parse(Marshal.PtrToStringUTF8((IntPtr)pointer)!); }
            finally
            {
                delegate* unmanaged<byte*, void> free = &Exports.Free;
                free(pointer);
            }
        }

        public JsonDocument Reconvert(string? text, string? reading)
        {
            delegate* unmanaged<IntPtr, byte*, byte*, byte*> reconvert = &Exports.Reconvert;
            var textPointer = text is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(text);
            var readingPointer = reading is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(reading);
            byte* pointer;
            try { pointer = reconvert(GCHandle.ToIntPtr(_handle), (byte*)textPointer, (byte*)readingPointer); }
            finally
            {
                Marshal.FreeCoTaskMem(textPointer);
                Marshal.FreeCoTaskMem(readingPointer);
            }
            Assert.True(pointer != null, "C ABI が有効な JSON を返す");
            try { return JsonDocument.Parse(Marshal.PtrToStringUTF8((IntPtr)pointer)!); }
            finally
            {
                delegate* unmanaged<byte*, void> free = &Exports.Free;
                free(pointer);
            }
        }

        public void SetCodeInput(int enabled)
        {
            delegate* unmanaged<IntPtr, int, void> setCodeInput = &Exports.SetCodeInput;
            setCodeInput(GCHandle.ToIntPtr(_handle), enabled);
        }

        public void SetCodeTerminal(int terminal)
        {
            delegate* unmanaged<IntPtr, int, void> setCodeTerminal = &Exports.SetCodeTerminal;
            setCodeTerminal(GCHandle.ToIntPtr(_handle), terminal);
        }

        public int SetCodeLine(int japanese, string? before = null)
        {
            delegate* unmanaged<IntPtr, int, byte*, int> setCodeLine = &Exports.SetCodeLine;
            var beforePointer = before is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(before);
            try { return setCodeLine(GCHandle.ToIntPtr(_handle), japanese, (byte*)beforePointer); }
            finally { Marshal.FreeCoTaskMem(beforePointer); }
        }

        public void CaretMoved(int passedVk, int control = 0)
        {
            delegate* unmanaged<IntPtr, int, int, void> caretMoved = &Exports.CaretMoved;
            caretMoved(GCHandle.ToIntPtr(_handle), passedVk, control);
        }

        public int CodeEnglishAt(string? before = null)
        {
            delegate* unmanaged<IntPtr, byte*, int> codeEnglishAt = &Exports.CodeEnglishAt;
            var beforePointer = before is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(before);
            try { return codeEnglishAt(GCHandle.ToIntPtr(_handle), (byte*)beforePointer); }
            finally { Marshal.FreeCoTaskMem(beforePointer); }
        }

        public void Type(string text, string? after = null)
        {
            foreach (var c in text)
            {
                using var result = Key(c, after: after);
            }
        }

        public void Dispose() => _handle.Free();
    }

    private static void AssertPassThrough(JsonDocument result, string? commit)
    {
        var root = result.RootElement;
        Assert.True(!root.GetProperty("consumed").GetBoolean(), "元の OS イベントを 1 回だけ通す");
        var commits = root.GetProperty("commits");
        Assert.Equal(commit is null ? 0 : 1, commits.GetArrayLength(), "確定を重複させない");
        if (commit is not null)
        {
            Assert.Equal(commit, commits[0].GetProperty("text").GetString());
            Assert.Equal(0, commits[0].GetProperty("deleteBefore").GetInt32());
        }
        Assert.Equal(JsonValueKind.Null, root.GetProperty("view").ValueKind, "未確定表示を閉じる");
    }

    [Test]
    public static void Native_CodeLineToggleControlsCompositionWithoutSurroundingText()
    {
        using var native = new NativeSession();
        native.SetCodeInput(1);
        using var direct = native.Key('k');
        AssertPassThrough(direct, null);
        using var newline = native.Key(0, VirtualKeys.Return);
        AssertPassThrough(newline, null);

        Assert.Equal(1, native.SetCodeLine(7), "明示的に日本語にすると、周辺文字列がなくても扱う");
        native.Type("kyouha");
        using var commit = native.Key(0, VirtualKeys.Return);
        Assert.True(commit.RootElement.GetProperty("consumed").GetBoolean(), "日本語の確定を扱う");
        Assert.Equal("きょうは", commit.RootElement.GetProperty("commits")[0].GetProperty("text").GetString());

        Assert.Equal(1, native.SetCodeLine(0), "英数へ戻す操作を扱う");
        using var english = native.Key('k');
        AssertPassThrough(english, null);
        Assert.Equal(0, native.SetCodeLine(0), "日本語でなければモード切り替えを扱わない");
    }

    [Test]
    public static void Native_CodeEnglishAtUsesUtf8AndTrackedContext()
    {
        using var native = new NativeSession();
        Assert.Equal(0, native.CodeEnglishAt("$ "), "コード入力でなければ英数の表示にしない");
        Assert.Equal(0, native.SetCodeLine(1), "コード入力以外のモード切り替えを扱わない");
        native.SetCodeInput(1);
        Assert.Equal(1, native.CodeEnglishAt("$ "), "シェルの行は英数");
        Assert.Equal(1, native.CodeEnglishAt(), "NULL は追跡している行を使う");

        Assert.Equal(0, native.SetCodeLine(1, "│ › 日本語😊 "), "UTF-8 の AI プロンプトでは切り替えを扱わない");
        Assert.Equal(0, native.CodeEnglishAt("│ › 日本語😊 "), "UTF-8 の AI プロンプトでは日本語");
        Assert.Equal(0, native.CodeEnglishAt(), "UTF-8 の行も次の問い合わせに引き継ぐ");
        Assert.Equal(0, native.SetCodeLine(1), "既に日本語の場所では切り替えを扱わない");
        Assert.Equal(0, native.CodeEnglishAt("printf \"日本語😊"), "文字列の中は日本語");
        Assert.Equal(1, native.CodeEnglishAt("printf \"日本語😊\""), "文字列の外は英数");

        Assert.Equal(1, native.SetCodeLine(1), "コードの行だけ明示的に日本語にする");
        Assert.Equal(0, native.CodeEnglishAt(), "明示的な日本語を表示に反映する");
        Assert.Equal(1, native.SetCodeLine(0), "明示的に英数へ戻す");
        Assert.Equal(1, native.CodeEnglishAt(), "英数への切り替えを表示に反映する");
    }

    [Test]
    public static void Native_PassedNavigationClearsExplicitJapanese()
    {
        using var native = new NativeSession();
        native.SetCodeInput(1);
        foreach (var (passedVk, control) in new[]
                 {
                     (VirtualKeys.Tab, 0), (0x21, 0), (0x22, 0), (0x23, 0), (0x24, 0),
                     (0x25, 0), (0x26, 0), (0x27, 0), (0x28, 0), (0x2E, 0), ('V', 7),
                 })
        {
            Assert.Equal(1, native.SetCodeLine(1, "$ "));
            native.CaretMoved('A');
            native.CaretMoved(VirtualKeys.Return);
            Assert.Equal(0, native.CodeEnglishAt(), "普通の入力と改行では日本語を続ける");
            native.CaretMoved(passedVk, control);
            Assert.Equal(1, native.CodeEnglishAt(), "アプリに通した移動操作で英数に戻す");
        }
    }

    [Test]
    public static void Native_TerminalJapaneseSurvivesNewlineAndOutputMovement()
    {
        using var native = new NativeSession();
        native.SetCodeInput(1);
        native.SetCodeTerminal(7);
        Assert.Equal(1, native.SetCodeLine(1));
        native.Type("kyouha");
        using var commit = native.Key(0, VirtualKeys.Return);
        Assert.Equal("きょうは", commit.RootElement.GetProperty("commits")[0].GetProperty("text").GetString());
        using var newline = native.Key(0, VirtualKeys.Return);
        AssertPassThrough(newline, null);
        native.CaretMoved(VirtualKeys.Return);
        Assert.Equal(0, native.CodeEnglishAt(), "改行しても明示的な日本語を続ける");
        native.CaretMoved(0);
        Assert.Equal(0, native.CodeEnglishAt(), "ターミナルの出力では明示的な日本語を解除しない");
        native.SetCodeTerminal(1);
        Assert.Equal(0, native.CodeEnglishAt(), "同じ入力欄の種類を再設定しても日本語を続ける");

        native.CaretMoved(0x26);
        Assert.Equal(1, native.CodeEnglishAt(), "ターミナルでも通した矢印キーで英数に戻す");
        Assert.Equal(1, native.SetCodeLine(1));
        native.SetCodeTerminal(0);
        Assert.Equal(1, native.CodeEnglishAt(), "エディターへ移ったら英数に戻す");
        Assert.Equal(1, native.SetCodeLine(1));
        native.CaretMoved(0);
        Assert.Equal(1, native.CodeEnglishAt(), "エディターのクリックでは英数に戻す");
    }

    [Test]
    public static void Native_OptionalCodeExportsIgnoreMissingSessions()
    {
        delegate* unmanaged<IntPtr, int, byte*, int> setCodeLine = &Exports.SetCodeLine;
        delegate* unmanaged<IntPtr, int, void> setCodeTerminal = &Exports.SetCodeTerminal;
        delegate* unmanaged<IntPtr, int, int, void> caretMoved = &Exports.CaretMoved;
        delegate* unmanaged<IntPtr, byte*, int> codeEnglishAt = &Exports.CodeEnglishAt;
        var nullSession = GCHandle.Alloc(null);
        var otherTarget = GCHandle.Alloc(new object());
        try
        {
            foreach (var handle in new[] { IntPtr.Zero, GCHandle.ToIntPtr(nullSession), GCHandle.ToIntPtr(otherTarget) })
            {
                Assert.Equal(0, setCodeLine(handle, 1, null), "セッションがなければモード切り替えを扱わない");
                Assert.Equal(0, setCodeLine(handle, 0, null));
                Assert.Equal(0, codeEnglishAt(handle, null), "セッションがなければ英数の表示にしない");
                setCodeTerminal(handle, 1);
                setCodeTerminal(handle, 0);
                caretMoved(handle, 0, 0);
                caretMoved(handle, 0x26, 1);
            }
        }
        finally
        {
            nullSession.Free();
            otherTarget.Free();
        }
    }

    [Test]
    public static void Native_BmpCombiningAccentPreservesLatinRaw()
    {
        foreach (var (raw, mark) in new[] { ("e", 0x0301), ("cafe", 0x0301), ("a", 0x0308) })
        {
            using var native = new NativeSession();
            native.Type(raw);
            using var result = native.Key(mark);
            AssertPassThrough(result, raw);
            Assert.True(!native.Session.IsComposing, "アクセントの前に原文を確定する");
            using var secondMark = native.Key(0x0300);
            AssertPassThrough(secondMark, null);
        }
    }

    [Test]
    public static void Native_CombiningMarksKeepExplicitJapaneseAndVariationForms()
    {
        foreach (var mark in new[] { 0x3099, 0xFE0F, 0xE0100 })
        {
            using var native = new NativeSession();
            native.Type("ka");
            using var result = native.Key(mark);
            AssertPassThrough(result, "か");
        }
        using var explicitKana = new NativeSession();
        explicitKana.Type("e");
        using var mode = explicitKana.Key(0, VirtualKeys.F6);
        using var accent = explicitKana.Key(0x0301);
        AssertPassThrough(accent, "え");
    }

    [Test]
    public static void Native_CombiningMarksAttachBeforeAutomaticEnglishSpacing()
    {
        foreach (var mark in new[] { 0x3099, 0xFE0F, 0xE0100 })
        {
            using var native = new NativeSession();
            native.Type("ka", after: "world");
            using var result = native.Key(mark);
            AssertPassThrough(result, "か");
        }
        using var explicitKana = new NativeSession();
        explicitKana.Type("e", after: "world");
        using var mode = explicitKana.Key(0, VirtualKeys.F6);
        using var accent = explicitKana.Key(0x0301);
        AssertPassThrough(accent, "え");
    }

    [Test]
    public static void Native_NormalCommitStillAddsEnglishSpacing()
    {
        using var native = new NativeSession();
        native.Type("ka", after: "world");
        using var result = native.Key(0, VirtualKeys.Return);
        Assert.True(result.RootElement.GetProperty("consumed").GetBoolean(), "通常の Enter は消費する");
        Assert.Equal("か ", result.RootElement.GetProperty("commits")[0].GetProperty("text").GetString());
    }

    [Test]
    public static void Native_SupplementaryScalarCommitsDisplayWithoutTruncating()
    {
        foreach (var scalar in new[] { 0x1F60A, 0x20BB7, 0x10000, 0x10FFFF })
        {
            using var native = new NativeSession();
            native.Type("kyouha");
            using var result = native.Key(scalar);
            AssertPassThrough(result, "きょうは");
            using var empty = native.Key(scalar);
            AssertPassThrough(empty, null);
        }
    }

    [Test]
    public static void Native_InvalidScalarLeavesCompositionUnchanged()
    {
        using var native = new NativeSession();
        using var initial = native.Key('e');
        var view = initial.RootElement.GetProperty("view").GetRawText();
        foreach (var scalar in new[] { -1, 0xD800, 0xDFFF, 0x110000, int.MaxValue })
        {
            using var result = native.Key(scalar);
            Assert.True(!result.RootElement.GetProperty("consumed").GetBoolean(), "不正なスカラーを通す");
            Assert.Equal(0, result.RootElement.GetProperty("commits").GetArrayLength());
            Assert.Equal(view, result.RootElement.GetProperty("view").GetRawText());
            Assert.True(native.Session.IsComposing, "未確定の内容を変えない");
        }
    }

    private static bool ContainsMacFunctionKeyScalar(string json) =>
        json.Any(c => c is >= '\uF700' and <= '\uF747');

    [Test]
    public static void Native_MacFunctionKeyScalarIsTreatedAsNoCharacter()
    {
        // macOS の矢印・Delete・PageDown・F8 は characters に U+F700..U+F747 を入れてくる。文字なし (0) と同じ結果になる。
        foreach (var (raw, scalar, vk) in new[]
        {
            ("aiueo", 0xF702, VirtualKeys.Left), ("hello", 0xF702, VirtualKeys.Left), ("hello", 0xF728, 0x2E),
            ("aiueo", 0xF72D, 0x22), ("aiueo", 0xF70B, 0x77), ("aiueo", 0xF700, VirtualKeys.Up), ("aiueo", 0xF747, 0x07),
        })
        {
            using var withScalar = new NativeSession();
            withScalar.Type(raw);
            using var withoutScalar = new NativeSession();
            withoutScalar.Type(raw);
            using var result = withScalar.Key(scalar, vk);
            using var expected = withoutScalar.Key(0, vk);
            var json = result.RootElement.GetRawText();
            Assert.True(!ContainsMacFunctionKeyScalar(json), "私用領域の文字を表示にも確定にも入れない");
            Assert.Equal(expected.RootElement.GetRawText(), json);
        }
    }

    [Test]
    public static void Native_MacLeftArrowCommitsLatinAndPassesThrough()
    {
        using var native = new NativeSession();
        native.Type("hello");
        using var result = native.Key(0xF702, VirtualKeys.Left);
        AssertPassThrough(result, "hello");
    }

    [Test]
    public static void Native_PrivateUseScalarOutsideFunctionKeyRangeStaysCharacter()
    {
        // U+F8FF (Apple ロゴ) や範囲の両隣は機能キーではないので、これまでどおり文字として扱う。
        foreach (var scalar in new[] { 0xF8FF, 0xF6FF, 0xF748 })
        {
            using var withScalar = new NativeSession();
            withScalar.Type("aiueo");
            using var withoutScalar = new NativeSession();
            withoutScalar.Type("aiueo");
            using var result = withScalar.Key(scalar, 0x07);
            using var noCharacter = withoutScalar.Key(0, 0x07);
            Assert.True(result.RootElement.GetRawText() != noCharacter.RootElement.GetRawText(), "範囲外は文字として渡る");
        }
    }

    [Test]
    public static void Native_ReconvertStartsAndCommitsThroughTheAbi()
    {
        using var native = new NativeSession();
        using var started = native.Reconvert("今日", "きょう");
        Assert.True(started.RootElement.GetProperty("consumed").GetBoolean(), "再変換を始めたらキーは使う");
        Assert.Equal(0, started.RootElement.GetProperty("commits").GetArrayLength());
        Assert.True(started.RootElement.GetProperty("view").GetProperty("converting").GetBoolean(), "変換中の表示を返す");
        // 元の文字のままなら確定しないので、候補を 1 つ選び直してから確定する
        using var down = native.Key('\0', VirtualKeys.Down);
        var shown = down.RootElement.GetProperty("view").GetProperty("text").GetString();
        Assert.True(shown != "今日", "候補を選び直した");
        using var enter = native.Key('\r', VirtualKeys.Return);
        var commits = enter.RootElement.GetProperty("commits");
        Assert.Equal(1, commits.GetArrayLength());
        Assert.Equal(shown, commits[0].GetProperty("text").GetString());
        Assert.Equal(0, commits[0].GetProperty("deleteBefore").GetInt32());
    }

    [Test]
    public static void Native_ReconvertRejectsEmptyOrNullArguments()
    {
        foreach (var (text, reading) in new (string?, string?)[] { (null, null), ("今日", null), (null, "きょう"), ("今日", "") })
        {
            using var native = new NativeSession();
            using var result = native.Reconvert(text, reading);
            Assert.True(!result.RootElement.GetProperty("consumed").GetBoolean(), "始めずにキーを通す");
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("view").ValueKind);
            Assert.True(!native.Session.IsComposing, "変換中にならない");
        }
    }
}
