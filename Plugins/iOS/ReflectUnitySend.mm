// Tiny C-ABI relay: the Swift bridge cannot reference UnitySendMessage directly
// (a @_silgen_name reference becomes a dynamic-lookup undefined that dyld can't bind,
// because UnitySendMessage lives in a static lib pulled in only by a NORMAL reference).
// This .mm makes that normal reference, so the linker force-links UnitySendMessage;
// the Swift side calls ReflectUnitySend (defined here, in-framework) instead.
extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);

extern "C" void ReflectUnitySend(const char* obj, const char* method, const char* msg) {
    UnitySendMessage(obj, method, msg);
}
