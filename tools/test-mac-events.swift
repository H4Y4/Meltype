// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

import Cocoa
import InputMethodKit
import Carbon.HIToolbox
final class EventClient: NSObject, IMKTextInput {
 var document=""; var marked=""
 // 選択範囲 (nil なら従来どおり、文書の終わりにキャレットがあるだけ)。marked は markedLocation の位置にある変換中の文字 (document には含めない)。
 var selection:NSRange?=nil; var markedLocation=0
 var caret:Int {selection?.location ?? (document as NSString).length}
 func text(_ value:Any?)->String { (value as? NSAttributedString)?.string ?? (value as? String ?? "") }
 func insertText(_ string: Any!, replacementRange: NSRange) {
  let value=text(string)
  if replacementRange.location != NSNotFound { document=(document as NSString).replacingCharacters(in:replacementRange,with:value) }
  else if !marked.isEmpty { document=(document as NSString).replacingCharacters(in:NSRange(location:markedLocation,length:0),with:value); selection=selection.map{_ in NSRange(location:markedLocation+(value as NSString).length,length:0)} }
  else if let selection { document=(document as NSString).replacingCharacters(in:selection,with:value); self.selection=NSRange(location:selection.location+(value as NSString).length,length:0) }
  else {document+=value}
  marked=""
 }
 func setMarkedText(_ string: Any!, selectionRange: NSRange, replacementRange: NSRange) {
  let value=text(string)
  // 変換中でなければ、今の選択範囲は変換中の文字に置き換わる (IMK の挙動)。
  if marked.isEmpty && !value.isEmpty { if let selection { document=(document as NSString).replacingCharacters(in:selection,with:""); markedLocation=selection.location; self.selection=NSRange(location:selection.location,length:0) } else { markedLocation=(document as NSString).length } }
  marked=value
 }
 func selectedRange()->NSRange {marked.isEmpty ? (selection ?? NSRange(location:(document as NSString).length,length:0)) : NSRange(location:markedLocation+(marked as NSString).length,length:0)}
 func markedRange()->NSRange {marked.isEmpty ? NSRange(location:NSNotFound,length:0):NSRange(location:markedLocation,length:(marked as NSString).length)}
 func attributedSubstring(from range:NSRange)->NSAttributedString! { let text=document as NSString; guard range.location != NSNotFound && range.location<=text.length else{return nil}; return NSAttributedString(string:text.substring(with:NSRange(location:range.location,length:min(range.length,text.length-range.location)))) }
 func length()->Int {(document as NSString).length}
 func characterIndex(for point:NSPoint, tracking mode:IMKLocationToOffsetMappingMode, inMarkedRange:UnsafeMutablePointer<ObjCBool>!)->Int {inMarkedRange?.pointee=false; return 0}
 func attributes(forCharacterIndex index:Int, lineHeightRectangle rectangle:UnsafeMutablePointer<NSRect>!)->[AnyHashable:Any]! {rectangle?.pointee=NSRect(x:0,y:0,width:1,height:20); return [:]}
 func validAttributesForMarkedText()->[Any]! {[]}
 func overrideKeyboard(withKeyboardNamed name:String!) {}
 func selectMode(_ identifier:String!) {}
 func supportsUnicode()->Bool {true}
 func bundleIdentifier()->String! {"local.meltype.event-client"}
 func windowLevel()->Int32 {0}
 func supportsProperty(_ property:TSMDocumentPropertyTag)->Bool {true}
 func uniqueClientIdentifierString()->String! {"synthetic-event-client"}
 func string(from range:NSRange, actualRange:UnsafeMutablePointer<NSRange>!)->String! {actualRange?.pointee=range;return attributedSubstring(from:range)?.string}
 func firstRect(forCharacterRange range:NSRange, actualRange:UnsafeMutablePointer<NSRange>!)->NSRect {actualRange?.pointee=range;return .zero}
}
#if !REAL_CONVERTER
final class MeltypeConverter {
 static let shared=MeltypeConverter()
 func clauses(for text:String,context:String?)->[(reading:String,text:String)] {[]}
 func candidates(for text:String)->[String] {[]}
}
#endif
var candidatesWindow: IMKCandidates? = nil
@main
struct EventTests {
 static func main() {
  let app=NSApplication.shared
  app.setActivationPolicy(.prohibited)
  guard let server=IMKServer(name:"LocalMeltypeBoundaryTests",bundleIdentifier:Bundle.main.bundleIdentifier) else {fatalError("IMKServer creation failed")}
  var passed=0
  func check(_ name:String,_ body:(MeltypeInputController,EventClient)->Void) {
   let client=EventClient()
   // IMKInputController の初期化は OS のクライアントプロキシ専用なので nil で初期化する。
   // handle / commitComposition には合成 IMKTextInput クライアントを明示して渡す。
   guard let controller=MeltypeInputController(server:server,delegate:nil,client:nil) else {fatalError("Controller creation failed")}
   body(controller,client); passed+=1; print("PASS: \(name)")
  }
  func key(_ controller:MeltypeInputController,_ client:EventClient,_ chars:String,code:UInt16=0,flags:NSEvent.ModifierFlags=[])->Bool {
   let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:flags,timestamp:0,windowNumber:0,context:nil,characters:chars,charactersIgnoringModifiers:chars,isARepeat:false,keyCode:code)!
   let consumed=controller.handle(event,client:client)
   if !consumed && !flags.contains(.command) && !flags.contains(.control) && chars.unicodeScalars.allSatisfy({$0.value>=0x20}) {
    // アプリが元イベントを受ける部分を 1 回だけ模擬する。
    client.insertText(chars,replacementRange:NSRange(location:NSNotFound,length:0))
   }
   return consumed
  }
  func type(_ controller:MeltypeInputController,_ client:EventClient,_ raw:String) {
   for scalar in raw.unicodeScalars { _=key(controller,client,String(scalar),code:UInt16(kVK_ANSI_A)) }
  }
  func equal(_ actual:String,_ expected:String) {
   precondition(Array(actual.unicodeScalars)==Array(expected.unicodeScalars),"expected \(expected.debugDescription), got \(actual.debugDescription)")
  }
  for (raw,event,expected) in [("e","\u{0301}\u{0300}","e\u{0301}\u{0300}"),("ka","\u{3099}\u{FE0F}","か\u{3099}\u{FE0F}"),("ka","\u{E0100}\u{E0101}","か\u{E0100}\u{E0101}"),("ka","😀","か😀"),("ka","👩‍👩‍👧‍👦","か👩‍👩‍👧‍👦"),("ka","e\u{0301}","かe\u{0301}")] {
   check("event \(event.debugDescription)") { controller,client in
    type(controller,client,raw)
    precondition(!key(controller,client,event),"external event must pass through")
    equal(client.document,expected); equal(client.marked,"")
   }
  }
  for action in ["Enter","Space","Tab","focus"] {
   check("protected \(action)") { controller,client in
    type(controller,client,"@kuraido")
    switch action {
    case "Enter": _=key(controller,client,"\r",code:UInt16(kVK_Return))
    case "Space": _=key(controller,client," ",code:UInt16(kVK_Space))
    case "Tab": _=key(controller,client,"\t",code:UInt16(kVK_Tab))
    default: controller.commitComposition(client)
    }
    equal(client.document,"@kuraido"+(action=="Space" ? " " : ""));equal(client.marked,"")
   }
  }
  for action in ["Enter","Space","Tab","focus"] {
   check("mixed protected \(action)") { controller,client in
    type(controller,client,"kyouha「@kuraido」ashita")
    switch action {
    case "Enter": _=key(controller,client,"\r",code:UInt16(kVK_Return))
    case "Space": _=key(controller,client," ",code:UInt16(kVK_Space));_=key(controller,client,"\r",code:UInt16(kVK_Return))
    case "Tab": _=key(controller,client,"\t",code:UInt16(kVK_Tab))
    default: controller.commitComposition(client)
    }
    #if REAL_CONVERTER
    precondition(client.document.contains("「@kuraido」"),"protected token changed")
    precondition(!client.document.contains("kyouha") && !client.document.contains("ashita"),"Japanese gaps skipped")
    #else
    equal(client.document,"きょうは「@kuraido」あした")
    #endif
    equal(client.marked,"")
   }
  }
  check("Backspace at protected boundary") { controller,client in
   type(controller,client,"./z]");_=key(controller,client,"",code:UInt16(kVK_Delete));equal(client.marked,"./z")
   _=key(controller,client,"\r",code:UInt16(kVK_Return));equal(client.document,"./z")
  }
  check("F6 override before marks") { controller,client in
   type(controller,client,"e");_=key(controller,client,"",code:UInt16(kVK_F6));_=key(controller,client,"\u{0301}\u{0300}");equal(client.document,"え\u{0301}\u{0300}")
  }
  check("F9 override") { controller,client in
   type(controller,client,"@kuraido");_=key(controller,client,"",code:UInt16(kVK_F9));_=key(controller,client,"\r",code:UInt16(kVK_Return));equal(client.document,"＠ｋｕｒａｉｄｏ")
  }
  check("JIS direct and kana modes") { controller,client in
   _=key(controller,client,"",code:UInt16(kVK_JIS_Eisu));type(controller,client,"ka");equal(client.document,"ka")
   _=key(controller,client,"",code:UInt16(kVK_JIS_Kana));type(controller,client,"ka");_=key(controller,client,"",code:UInt16(kVK_F6));_=key(controller,client,"\r",code:UInt16(kVK_Return));equal(client.document,"ka か")
  }
  check("Command shortcut commits once") { controller,client in
   type(controller,client,"@kuraido");precondition(!key(controller,client,"a",flags:.command));equal(client.document,"@kuraido");equal(client.marked,"")
  }
  // ---- 選択した文字の再変換 (Control+Shift+R) ----
  // 変換エンジンの結果には依存せず、「変換中の文字になり、Enter で選択範囲を置き換える・Esc で元に戻る」を確かめる。
  func reconvert(_ controller:MeltypeInputController,_ client:EventClient)->Bool {
   key(controller,client,"\u{12}",code:UInt16(kVK_ANSI_R),flags:[.control,.shift])
  }
  for (original,label) in [("今日","漢字"),("きょう","ひらがな"),("キョウ","カタカナ")] {
   check("reconversion Enter replaces selection (\(label))") { controller,client in
    client.document="さっき"+original+"は晴れ"; client.selection=NSRange(location:3,length:(original as NSString).length)
    precondition(reconvert(controller,client),"Control+Shift+R with a selection must be consumed")
    precondition(!client.marked.isEmpty,"reconversion must show marked text"); equal(client.document,"さっきは晴れ")
    let shown=client.marked
    precondition(key(controller,client,"\r",code:UInt16(kVK_Return)),"Enter must commit")
    equal(client.document,"さっき"+shown+"は晴れ"); equal(client.marked,"")
   }
   check("reconversion Esc restores selection text (\(label))") { controller,client in
    client.document="さっき"+original+"は晴れ"; client.selection=NSRange(location:3,length:(original as NSString).length)
    precondition(reconvert(controller,client),"must start")
    precondition(key(controller,client,"\u{1B}",code:UInt16(kVK_Escape)),"Esc must cancel")
    equal(client.document,"さっき"+original+"は晴れ"); equal(client.marked,"")
    // 取り消したあとは普通に入力できる
    type(controller,client,"ka");_=key(controller,client,"\r",code:UInt16(kVK_Return));precondition(client.document.contains("か"),"typing works after cancel")
   }
  }
  check("reconversion Backspace until empty restores selection text") { controller,client in
   client.document="今日は"; client.selection=NSRange(location:0,length:2)
   precondition(reconvert(controller,client),"must start")
   for _ in 0..<10 where !client.marked.isEmpty { _=key(controller,client,"",code:UInt16(kVK_Delete)) }
   equal(client.marked,""); equal(client.document,"今日は")
  }
  check("reconversion focus loss commits the replacement") { controller,client in
   client.document="今日は"; client.selection=NSRange(location:0,length:2)
   precondition(reconvert(controller,client),"must start")
   let shown=client.marked; controller.commitComposition(client)
   equal(client.document,shown+"は"); equal(client.marked,"")
  }
  for (document,selection,label) in [("今日",nil as NSRange?,"no selection"),("今日",NSRange(location:2,length:0),"caret only"),("hello",NSRange(location:0,length:5),"latin"),("今日 abc",NSRange(location:0,length:6),"mixed latin"),("今日\n明日",NSRange(location:0,length:5),"newline"),(String(repeating:"あ",count:129),NSRange(location:0,length:129),"too long")] {
   check("reconversion passes through: \(label)") { controller,client in
    client.document=document; client.selection=selection
    precondition(!reconvert(controller,client),"Control+Shift+R must reach the app")
    equal(client.document,document); equal(client.marked,"")
   }
  }
  check("reconversion is not started in direct mode") { controller,client in
   _=key(controller,client,"",code:UInt16(kVK_JIS_Eisu))
   client.document="今日"; client.selection=NSRange(location:0,length:2)
   precondition(!reconvert(controller,client),"direct mode must pass Control+Shift+R"); equal(client.document,"今日")
  }
  // 読みの推定の単体確認 (macOS の CFStringTokenizer の辞書に依存する)
  check("reading estimation") { _,_ in
   var lines:[String]=[]
   for (text,expected) in [("今日","きょう"),("東京","とうきょう"),("食べる","たべる"),("学校へ行く","がっこうへいく"),("今日は","きょうは"),("大阪","おおさか"),("きょう","きょう"),("キョウ","きょう"),("コーヒー","こーひー"),("東京タワー","とうきょうたわー"),("今日、東京","きょう、とうきょう"),("「今日」","「きょう」")] {
    let actual=Reconversion.reading(of:text); lines.append("\(text) -> \(actual ?? "nil")")
    precondition(actual==expected,"reading of \(text): expected \(expected), got \(actual ?? "nil")")
   }
   for text in ["","abc","今日 abc","a今日","今日\n明日","１２３","今日 ","　"] {
    let actual=Reconversion.reading(of:text); lines.append("\(text.debugDescription) -> \(actual ?? "nil")")
    precondition(actual==nil,"reading of \(text.debugDescription) must be nil, got \(actual ?? "nil")")
   }
   print(lines.joined(separator:"\n"))
  }
  #if REAL_CONVERTER
  let converter=MeltypeConverter.shared
  let candidates=converter.candidates(for:"きょう")
  precondition(candidates.contains("今日"),"real dictionary conversion missing: \(candidates)")
  let clauses=converter.clauses(for:"きょうはいいてんき",context:nil)
  precondition(clauses.map(\.reading).joined()=="きょうはいいてんき","real clause readings lost")
  precondition(clauses.map(\.text).joined().contains("今日"),"real clause conversion missing")
  guard let directory=NativeCore.shared.dataDirectory else {fatalError("missing data directory")}
  precondition(FileManager.default.fileExists(atPath:directory+"/azooKey"),"converter ignored isolated data directory")
  passed+=3
  print("PASS: actual azooKey candidates, clause reading coverage, isolated data directory")
  #endif
  print("\(passed) Mac event/integration cases passed (synthetic client; OS IME registration and app GUI NOT_RUN)")
 }
}
