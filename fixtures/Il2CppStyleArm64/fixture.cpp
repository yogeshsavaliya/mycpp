// IL2CPP-shaped regression fixture. Hand-written in the shape of IL2CPP generated C++
// (hidden MethodInfo*, NullCheck, base constructor tail call). It is not Unity output
// and it is not a game. Equivalent C#:
//
//   public class Fixture : MonoBehaviour {
//       public int count; public float speed; public bool flag; public Fixture next; public int total;
//       public Fixture() { speed = 5f; count = 3; }
//       public void SmoothFollow() { count = count * 2 + 1; }
//       public void LateUpdate() { SmoothFollow(); }
//       public void Tick() { count += 1; SmoothFollow(); }
//       public int SumNext() { return next.count + count; }
//       public void Clamp(int v) { if (v > 10) v = 10; else if (v < 0) v = 0; total = v + count; }
//       public float Scaled(float dt) { return speed * dt; }
//       public void Toggle() { if (flag) count = count + 5; else total = total - 1; SmoothFollow(); }
//       public int Pick(int a, int b) { int r = a < b ? a * 3 : b - a; return r + count; }
//       public int NextTotal() { return next.total; }
//       public void CopyToNext() { next.count = count; }
//   }
struct MethodInfo;
struct Il2CppObject { void* klass; void* monitor; };
struct MonoBehaviour_t : Il2CppObject { long m_CachedPtr; };
struct Fixture_t : MonoBehaviour_t {
    int ___count;
    float ___speed;
    bool ___flag;
    Fixture_t* ___next;
    int ___total;
};
#define NOINLINE __attribute__((noinline))
extern "C" {
NOINLINE void MonoBehaviour__ctor_m(MonoBehaviour_t*, const MethodInfo*);
__attribute__((noreturn, noinline)) void il2cpp_codegen_raise_null_reference_exception();
}
static inline void NullCheck(void* p) { if (!p) il2cpp_codegen_raise_null_reference_exception(); }

extern "C" {
NOINLINE void Fixture_SmoothFollow_m(Fixture_t* __this, const MethodInfo*) {
    __this->___count = __this->___count * 2 + 1;
}
NOINLINE void Fixture_LateUpdate_m(Fixture_t* __this, const MethodInfo* method) {
    Fixture_SmoothFollow_m(__this, method);
}
NOINLINE void Fixture_Tick_m(Fixture_t* __this, const MethodInfo* method) {
    __this->___count = __this->___count + 1;
    Fixture_SmoothFollow_m(__this, method);
}
NOINLINE void Fixture__ctor_m(Fixture_t* __this, const MethodInfo* method) {
    __this->___speed = 5.0f;
    __this->___count = 3;
    MonoBehaviour__ctor_m(__this, method);
}
NOINLINE int Fixture_SumNext_m(Fixture_t* __this, const MethodInfo*) {
    Fixture_t* n = __this->___next;
    NullCheck(n);
    return n->___count + __this->___count;
}
NOINLINE void Fixture_Clamp_m(Fixture_t* __this, int v, const MethodInfo*) {
    if (v > 10) v = 10; else if (v < 0) v = 0;
    __this->___total = v + __this->___count;
}
NOINLINE float Fixture_Scaled_m(Fixture_t* __this, float dt, const MethodInfo*) {
    return __this->___speed * dt;
}
NOINLINE void Fixture_Toggle_m(Fixture_t* __this, const MethodInfo* method) {
    if (__this->___flag) __this->___count = __this->___count + 5;
    else __this->___total = __this->___total - 1;
    Fixture_SmoothFollow_m(__this, method);
}
NOINLINE int Fixture_Pick_m(Fixture_t* __this, int a, int b, const MethodInfo*) {
    int r = a < b ? a * 3 : b - a;
    return r + __this->___count;
}
NOINLINE int Fixture_NextTotal_m(Fixture_t* __this, const MethodInfo*) {
    Fixture_t* n = __this->___next;
    NullCheck(n);
    return n->___total;
}
NOINLINE void Fixture_CopyToNext_m(Fixture_t* __this, const MethodInfo*) {
    Fixture_t* n = __this->___next;
    NullCheck(n);
    n->___count = __this->___count;
}
}
