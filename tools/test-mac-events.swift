// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

import Cocoa
import InputMethodKit
import Carbon.HIToolbox
final class EventClient: NSObject, IMKTextInput {
 var document=""; var marked=""
 func text(_ value:Any?)->String { (value as? NSAttributedString)?.string ?? (value as? String ?? "") }
 func insertText(_ string: Any!, replacementRange: NSRange) { let value=text(string); if replacementRange.location != NSNotFound { document=(document as NSString).replacingCharacters(in:replacementRange,with:value) } else {document+=value}; marked="" }
 func setMarkedText(_ string: Any!, selectionRange: NSRange, replacementRange: NSRange) {marked=text(string)}
 func selectedRange()->NSRange {NSRange(location:(document as NSString).length,length:0)}
 func markedRange()->NSRange {marked.isEmpty ? NSRange(location:NSNotFound,length:0):NSRange(location:(document as NSString).length,length:(marked as NSString).length)}
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
 func clauses(for text:String,context:String?)->[(reading:String,text:String)] {text=="つくえ" ? [(reading:text,text:"机")] : []}
 // ページ送りのテスト用に、つくえ だけ 30 個の候補を返す (他の読みは今までどおり空)。
 func candidates(for text:String)->[String] {text=="つくえ" ? (1...30).map {"候補\($0)"} : []}
}
#endif
// 候補ウィンドウの実物 (IMKCandidates) は画面が無いと動かせないので、moveDown / moveUp の回数から選択の位置を数える偽物に差し替える。
// 実物が端で止まるか回るかはここでは確かめられない (InputController.selectInWindow は端を越える動きをしない前提)。
final class RecordingCandidates: IMKCandidates {
 var position=0; var shown=false
 override func update() {position=0}
 override func show(_ hint:IMKCandidatesLocationHint) {shown=true}
 override func hide() {shown=false}
 override func isVisible()->Bool {shown}
 override func moveDown(_ sender:Any?) {position+=1}
 override func moveUp(_ sender:Any?) {position-=1}
}
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
  #if !REAL_CONVERTER
  // PageDown / PageUp / Shift+↓↑ のページ送りで、候補ウィンドウの選択が本体の選択からずれないこと (9 個以上の移動、最後⇔最初の回り込み)。
  // NSEvent の characters は空にして送る (機能キーの私用領域の文字を避けるため)。
  check("candidate paging keeps window selection in sync") { controller,client in
   // 変換エンジンの関数を登録するのは main.swift なので、ここでも登録する (他の確認に影響しないよう、このテストを最後に置く)。
   NativeCore.shared.initialize()
   let window=RecordingCandidates(server:server,panelType:kIMKSingleColumnScrollingCandidatePanel)!
   candidatesWindow=window
   defer {candidatesWindow=nil}
   type(controller,client,"tsukue")
   _=key(controller,client," ",code:UInt16(kVK_Space))
   // 変換エンジンの候補は最初の候補送りで足されるので、↓ ↑ で先頭に戻しておく。
   _=key(controller,client,"",code:UInt16(kVK_DownArrow)); _=key(controller,client,"",code:UInt16(kVK_UpArrow))
   let list=(controller.candidates(nil) as? [String]) ?? []
   precondition(list.count>18,"need 3+ pages: \(list)")
   // 一覧を作り直した直後の移動は次の周回に回すので、回してから確かめる。
   RunLoop.current.run(until:Date(timeIntervalSinceNow:0.05))
   precondition(window.shown && window.position==0,"window starts at head")
   let size=9; let pages=(list.count+size-1)/size
   var page=0
   for step in ["PageDown","PageDown","PageUp","shiftDown","shiftUp","PageUp","PageUp"]+Array(repeating:"PageDown",count:pages) {
    switch step {
    case "PageDown": _=key(controller,client,"",code:UInt16(kVK_PageDown)); page=(page+1)%pages
    case "PageUp": _=key(controller,client,"",code:UInt16(kVK_PageUp)); page=(page+pages-1)%pages
    case "shiftDown": _=key(controller,client,"",code:UInt16(kVK_DownArrow),flags:.shift); page=(page+1)%pages
    default: _=key(controller,client,"",code:UInt16(kVK_UpArrow),flags:.shift); page=(page+pages-1)%pages
    }
    precondition(window.position==page*size,"\(step): window at \(window.position), expected \(page*size)")
   }
   // 本体の選択もウィンドウと同じ位置か: 数字キーの 1 で、そのページの先頭の候補が確定する。
   _=key(controller,client,"",code:UInt16(kVK_PageDown)); page=(page+1)%pages
   _=key(controller,client,"1",code:UInt16(kVK_ANSI_1))
   equal(client.document,list[page*size])
  }
  #endif
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
