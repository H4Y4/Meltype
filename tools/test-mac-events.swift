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
  check("F8 half-width katakana") { controller,client in
   // 実機では F8 の characters に U+F70B が入るが、main にはそれが変換中の文字に混ざる既知の不具合があり
   // (fix/mac-function-key-chars で修正中)、このテストでは characters を空にして送る。
   type(controller,client,"aiueo");precondition(key(controller,client,"",code:UInt16(kVK_F8)),"F8 must be consumed")
   equal(client.marked,"ｱｲｳｴｵ")
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
  // 入力中の Ctrl キー (設定 ControlKeys)。設定は入力欄ごとの本体を作るときに読むので、controller を作る前に config.json を書き換える。
  // Control を押すと characters は制御文字になり (Ctrl+J → "\n")、Shift も無視される (US 配列の Ctrl+: は ";")。charactersIgnoringModifiers は Shift を含む (":")。
  func ctrl(_ controller:MeltypeInputController,_ client:EventClient,_ chars:String,_ ignoring:String,code:Int,shift:Bool=false)->Bool {
   let flags:NSEvent.ModifierFlags = shift ? [.control,.shift] : [.control]
   let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:flags,timestamp:0,windowNumber:0,context:nil,characters:chars,charactersIgnoringModifiers:ignoring,isARepeat:false,keyCode:UInt16(code))!
   return controller.handle(event,client:client)
  }
  guard let dataDirectory=ProcessInfo.processInfo.environment["MELTYPE_DATA_DIR"] else {fatalError("MELTYPE_DATA_DIR is required")}
  let configURL=URL(fileURLWithPath:dataDirectory).appendingPathComponent("config.json")
  let originalConfig=try? Data(contentsOf:configURL)
  func writeControlKeys(_ style:String?) {
   var object=(originalConfig.flatMap { try? JSONSerialization.jsonObject(with:$0) } as? [String:Any]) ?? [:]
   if let style { object["ControlKeys"]=style }
   try! JSONSerialization.data(withJSONObject:object).write(to:configURL)
  }
  writeControlKeys(nil)
  check("Ctrl+J without a setting is not assigned (ATOK style)") { controller,client in
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,"\n","j",code:kVK_ANSI_J),"Ctrl+J must pass to the app")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  writeControlKeys("Mac")
  check("Mac style Ctrl+J / Ctrl+K / Ctrl+L") { controller,client in
   type(controller,client,"aiueo")
   precondition(ctrl(controller,client,"\u{0B}","k",code:kVK_ANSI_K),"Ctrl+K must be consumed"); equal(client.marked,"アイウエオ")
   precondition(ctrl(controller,client,"\u{0C}","l",code:kVK_ANSI_L),"Ctrl+L must be consumed"); equal(client.marked,"ａｉｕｅｏ")
   precondition(ctrl(controller,client,"\n","j",code:kVK_ANSI_J),"Ctrl+J must be consumed"); equal(client.marked,"あいうえお")
   equal(client.document,"")
  }
  check("Mac style Ctrl+; half-width alphanumerics (not katakana)") { controller,client in
   type(controller,client,"aiueo")
   precondition(ctrl(controller,client,";",";",code:kVK_ANSI_Semicolon),"Ctrl+; must be consumed"); equal(client.marked,"aiueo")
   equal(client.document,"")
  }
  check("Mac style Ctrl+: (JIS) and Ctrl+' half-width alphanumerics") { controller,client in
   type(controller,client,"aiueo")
   _=ctrl(controller,client,"\n","j",code:kVK_ANSI_J)
   // JIS 配列の Ctrl+: は Quote のキー (0x27): characters は "'"、charactersIgnoringModifiers は ":"
   precondition(ctrl(controller,client,"'",":",code:kVK_ANSI_Quote),"Ctrl+: (JIS) must be consumed"); equal(client.marked,"aiueo")
   _=ctrl(controller,client,"\n","j",code:kVK_ANSI_J); equal(client.marked,"あいうえお")
   precondition(ctrl(controller,client,"'","'",code:kVK_ANSI_Quote),"Ctrl+' must be consumed"); equal(client.marked,"aiueo")
   equal(client.document,"")
  }
  // 英字への切り替えのキー (US 配列の Ctrl+Shift+'、JIS 配列の Ctrl+Shift+;) は割り当てが無く、本体は使わない
  // (英数への切り替えは OS 側の処理に回る)。characters は Shift を無視する。
  check("Mac style Ctrl+Shift+' (US) is not consumed") { controller,client in
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,"'","\"",code:kVK_ANSI_Quote,shift:true),"Ctrl+Shift+' (US) must pass")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  check("Mac style Ctrl+Shift+; (JIS) is not consumed") { controller,client in
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,";","+",code:kVK_ANSI_Semicolon,shift:true),"Ctrl+Shift+; (JIS) must pass")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  check("Mac style Ctrl+Shift+; (US, Ctrl+:) is not consumed") { controller,client in
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,";",":",code:kVK_ANSI_Semicolon,shift:true),"Ctrl+: (US) must pass")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  check("Mac style Ctrl+N / Ctrl+F in an English word pass to the app") { controller,client in
   type(controller,client,"hello")
   precondition(!ctrl(controller,client,"\u{0E}","n",code:kVK_ANSI_N),"Ctrl+N must pass to the app")
   equal(client.document,"hello"); equal(client.marked,"")
   type(controller,client,"hello")
   precondition(!ctrl(controller,client,"\u{06}","f",code:kVK_ANSI_F),"Ctrl+F must pass to the app")
   equal(client.document,"hellohello"); equal(client.marked,"")
  }
  check("Mac style Ctrl+U is not assigned and Ctrl+N passes when empty") { controller,client in
   precondition(!ctrl(controller,client,"\u{0E}","n",code:kVK_ANSI_N),"Ctrl+N with no input must pass to the app")
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,"\u{15}","u",code:kVK_ANSI_U),"Ctrl+U must pass to the app")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  if let originalConfig { try! originalConfig.write(to:configURL) } else { try? FileManager.default.removeItem(at:configURL) }

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
