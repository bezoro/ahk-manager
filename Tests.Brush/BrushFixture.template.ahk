@@ADAPTED_SOURCE@@

; Observations and payloads below stay entirely inside this fixture.
global BrushFixtureState := {Active: true, Name: "", Failure: "", Payload: ""}

BrushFixtureObserveActive(query) {
    global BrushFixtureState
    if query != "A"
        throw Error("Unexpected query")
    if BrushFixtureState.Failure = "active"
        throw Error("Scripted observation failure")
    return BrushFixtureState.Active ? 1234 : 0
}

BrushFixtureObserveProcessName(hwnd) {
    global BrushFixtureState
    if hwnd != 1234
        throw Error("Unexpected handle")
    if BrushFixtureState.Failure = "name"
        throw Error("Scripted observation failure")
    return BrushFixtureState.Name
}

BrushFixtureSink(payload) {
    global BrushFixtureState
    BrushFixtureState.Payload := payload
}

BrushFixtureRouteSmaller() {
    if IsPaintingApp()
        BrushFixtureSink(@@SMALLER@@)
    else
        BrushFixtureSink(@@VOLUME_DOWN@@)
}

BrushFixtureRouteLarger() {
    if IsPaintingApp()
        BrushFixtureSink(@@LARGER@@)
    else
        BrushFixtureSink(@@VOLUME_UP@@)
}

BrushFixtureSet(name, active := true, failure := "") {
    global BrushFixtureState
    BrushFixtureState.Name := name
    BrushFixtureState.Active := active
    BrushFixtureState.Failure := failure
    BrushFixtureState.Payload := ""
}

BrushFixtureAssertPayload(expected) {
    global BrushFixtureState
    if BrushFixtureState.Payload !== expected
        throw Error("Payload mismatch")
}

BrushFixtureClassify(name, active, failure, expected) {
    BrushFixtureSet(name, active, failure)
    if IsPaintingApp() != expected
        throw Error("Classification mismatch")
    BrushFixtureRouteSmaller()
    BrushFixtureAssertPayload(expected ? "{Blind}[" : "{Volume_Down}")
}

; Even this local-only fixture must execute with a normal token.
if A_IsAdmin {
    FileAppend("FAIL brush-functional elevated`n", "**", "UTF-8-RAW")
    ExitApp(1)
}
try {
    routing := 0
    classification := 0
    if PaintingApps.Count != 7
        throw Error("App count mismatch")
    for name, enabled in PaintingApps {
        if enabled != 1
            throw Error("Map value mismatch")
        BrushFixtureSet(name)
        BrushFixtureRouteSmaller()
        BrushFixtureAssertPayload("{Blind}[")
        routing += 1
        BrushFixtureSet(name)
        BrushFixtureRouteLarger()
        BrushFixtureAssertPayload("{Blind}]")
        routing += 1
    }
    BrushFixtureSet("notepad.exe")
    BrushFixtureRouteSmaller()
    BrushFixtureAssertPayload("{Volume_Down}")
    routing += 1
    BrushFixtureSet("notepad.exe")
    BrushFixtureRouteLarger()
    BrushFixtureAssertPayload("{Volume_Up}")
    routing += 1

    BrushFixtureClassify("BLENDER.EXE", true, "", true)
    classification += 1
    BrushFixtureClassify("ClipStudioPaint.EXE", true, "", true)
    classification += 1
    BrushFixtureClassify("unknown.exe", true, "", false)
    classification += 1
    BrushFixtureClassify("blender.exe", false, "", false)
    classification += 1
    BrushFixtureClassify("blender.exe", true, "active", false)
    classification += 1
    BrushFixtureClassify("blender.exe", true, "name", false)
    classification += 1
    if routing != 16 || classification != 6
        throw Error("Assertion count mismatch")
    FileAppend("PASS brush-functional count=16 classification=6`n", "*", "UTF-8-RAW")
    ExitApp(0)
} catch {
    FileAppend("FAIL brush-functional`n", "**", "UTF-8-RAW")
    ExitApp(1)
}
