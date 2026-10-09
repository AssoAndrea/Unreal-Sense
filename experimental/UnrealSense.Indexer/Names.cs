using System;
using System.Threading;

namespace UnrealSense.Indexer
{
    /// <summary>
    /// Global, thread-safe identifier interning (ASCII/UTF-8 bytes → int id). Sharded open addressing: lookups from
    /// 32 threads rarely contend. Id = shard | (index &lt;&lt; ShardBits).
    /// </summary>
    public static class Names
    {
        const int ShardBits = 8;
        const int ShardCount = 1 << ShardBits;

        sealed class Shard
        {
            public int[] Slots = new int[1024];   // 0 = empty, else localIndex + 1
            public int[] Hashes = new int[1024];
            public volatile string[] Strings = new string[512];
            public int Count;
        }

        static readonly Shard[] shards = CreateShards();

        static Shard[] CreateShards()
        {
            var s = new Shard[ShardCount];
            for (int i = 0; i < s.Length; i++) s[i] = new Shard();
            // id 0 means "anonymous" everywhere: never give it to a real identifier
            s[0].Strings[0] = ""; s[0].Count = 1;
            return s;
        }

        public static int Hash(ReadOnlySpan<byte> s)
        {
            uint h = 2166136261;
            foreach (var b in s) h = (h ^ b) * 16777619;
            return (int)(h & 0x7fffffff);
        }

        public static int Intern(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            return Intern(bytes);
        }

        public static int Intern(ReadOnlySpan<byte> s) => Intern(s, Hash(s));

        public static int Intern(ReadOnlySpan<byte> s, int hash)
        {
            var shard = shards[hash & (ShardCount - 1)];
            lock (shard)
            {
                var slots = shard.Slots;
                int mask = slots.Length - 1;
                int i = (hash >> ShardBits) & mask;
                var strings = shard.Strings;
                while (true)
                {
                    int v = slots[i];
                    if (v == 0) break;
                    if (shard.Hashes[i] == hash && Equal(strings[v - 1], s))
                        return (hash & (ShardCount - 1)) | ((v - 1) << ShardBits);
                    i = (i + 1) & mask;
                }
                int index = shard.Count++;
                if (index >= strings.Length)
                {
                    var grown = new string[strings.Length * 2];
                    Array.Copy(strings, grown, strings.Length);
                    shard.Strings = strings = grown;
                }
                strings[index] = System.Text.Encoding.UTF8.GetString(s);
                slots[i] = index + 1;
                shard.Hashes[i] = hash;
                if (shard.Count * 2 > slots.Length) Grow(shard);
                return (hash & (ShardCount - 1)) | (index << ShardBits);
            }
        }

        static void Grow(Shard shard)
        {
            var oldSlots = shard.Slots; var oldHashes = shard.Hashes;
            var slots = new int[oldSlots.Length * 2]; var hashes = new int[slots.Length];
            int mask = slots.Length - 1;
            for (int j = 0; j < oldSlots.Length; j++)
            {
                if (oldSlots[j] == 0) continue;
                int i = (oldHashes[j] >> ShardBits) & mask;
                while (slots[i] != 0) i = (i + 1) & mask;
                slots[i] = oldSlots[j]; hashes[i] = oldHashes[j];
            }
            shard.Slots = slots; shard.Hashes = hashes;
        }

        static bool Equal(string a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length)
            {
                // non-ASCII: compare via UTF-8
                return System.Text.Encoding.UTF8.GetByteCount(a) == b.Length && System.Text.Encoding.UTF8.GetString(b) == a;
            }
            for (int i = 0; i < b.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static string Get(int id)
        {
            if (id < 0) return "?";
            var shard = shards[id & (ShardCount - 1)];
            return shard.Strings[id >> ShardBits];
        }

        public static int Count
        {
            get { int n = 0; foreach (var s in shards) n += s.Count; return n; }
        }
    }

    /// <summary>Pre-interned identifiers the parser compares against.</summary>
    public static class K
    {
        public static readonly int
            Class = Names.Intern("class"), Struct = Names.Intern("struct"), Union = Names.Intern("union"), Enum = Names.Intern("enum"),
            Namespace = Names.Intern("namespace"), Template = Names.Intern("template"), Typename = Names.Intern("typename"),
            Typedef = Names.Intern("typedef"), Using = Names.Intern("using"), Public = Names.Intern("public"), Private = Names.Intern("private"),
            Protected = Names.Intern("protected"), Virtual = Names.Intern("virtual"), Static = Names.Intern("static"), Inline = Names.Intern("inline"),
            Const = Names.Intern("const"), Constexpr = Names.Intern("constexpr"), Consteval = Names.Intern("consteval"), Constinit = Names.Intern("constinit"),
            Volatile = Names.Intern("volatile"), Mutable = Names.Intern("mutable"), Explicit = Names.Intern("explicit"), Extern = Names.Intern("extern"),
            Friend = Names.Intern("friend"), Operator = Names.Intern("operator"), Override = Names.Intern("override"), Final = Names.Intern("final"),
            Noexcept = Names.Intern("noexcept"), ThreadLocal = Names.Intern("thread_local"), Register = Names.Intern("register"),
            Auto = Names.Intern("auto"), Decltype = Names.Intern("decltype"), Sizeof = Names.Intern("sizeof"), Alignof = Names.Intern("alignof"),
            Alignas = Names.Intern("alignas"), StaticAssert = Names.Intern("static_assert"), Requires = Names.Intern("requires"), Concept = Names.Intern("concept"),
            If = Names.Intern("if"), Else = Names.Intern("else"), For = Names.Intern("for"), While = Names.Intern("while"), Do = Names.Intern("do"),
            Switch = Names.Intern("switch"), Case = Names.Intern("case"), Default = Names.Intern("default"), Return = Names.Intern("return"),
            Break = Names.Intern("break"), Continue = Names.Intern("continue"), Goto = Names.Intern("goto"), Try = Names.Intern("try"), Catch = Names.Intern("catch"),
            Throw = Names.Intern("throw"), New = Names.Intern("new"), Delete = Names.Intern("delete"), This = Names.Intern("this"),
            True = Names.Intern("true"), False = Names.Intern("false"), Nullptr = Names.Intern("nullptr"), NULL = Names.Intern("NULL"),
            StaticCast = Names.Intern("static_cast"), DynamicCast = Names.Intern("dynamic_cast"), ReinterpretCast = Names.Intern("reinterpret_cast"),
            ConstCast = Names.Intern("const_cast"), Typeid = Names.Intern("typeid"), CoReturn = Names.Intern("co_return"), CoAwait = Names.Intern("co_await"),
            Void = Names.Intern("void"), Bool = Names.Intern("bool"), Char = Names.Intern("char"), WcharT = Names.Intern("wchar_t"), Char8 = Names.Intern("char8_t"),
            Char16 = Names.Intern("char16_t"), Char32 = Names.Intern("char32_t"), Short = Names.Intern("short"), Int = Names.Intern("int"), Long = Names.Intern("long"),
            Float = Names.Intern("float"), Double = Names.Intern("double"), Signed = Names.Intern("signed"), Unsigned = Names.Intern("unsigned"),
            Int64 = Names.Intern("__int64"), Int32k = Names.Intern("__int32"), Int16k = Names.Intern("__int16"), Int8k = Names.Intern("__int8"),
            Declspec = Names.Intern("__declspec"), Forceinline = Names.Intern("__forceinline"), Restrict = Names.Intern("__restrict"), Cdecl = Names.Intern("__cdecl"),
            Stdcall = Names.Intern("__stdcall"), Fastcall = Names.Intern("__fastcall"), Vectorcall = Names.Intern("__vectorcall"), Attribute = Names.Intern("__attribute__"),
            Pragma = Names.Intern("__pragma"), PragmaOp = Names.Intern("_Pragma"), Asm = Names.Intern("__asm"),
            Defined = Names.Intern("defined"), HasInclude = Names.Intern("__has_include"), HasIncludeNext = Names.Intern("__has_include_next"),
            // Unreal
            UCLASS = Names.Intern("UCLASS"), USTRUCT = Names.Intern("USTRUCT"), UENUM = Names.Intern("UENUM"), UINTERFACE = Names.Intern("UINTERFACE"),
            UFUNCTION = Names.Intern("UFUNCTION"), UPROPERTY = Names.Intern("UPROPERTY"), UPARAM = Names.Intern("UPARAM"), UMETA = Names.Intern("UMETA"),
            UDELEGATE = Names.Intern("UDELEGATE"),
            GENERATED_BODY = Names.Intern("GENERATED_BODY"), GENERATED_UCLASS_BODY = Names.Intern("GENERATED_UCLASS_BODY"),
            GENERATED_USTRUCT_BODY = Names.Intern("GENERATED_USTRUCT_BODY"), GENERATED_IINTERFACE_BODY = Names.Intern("GENERATED_IINTERFACE_BODY"),
            GENERATED_UINTERFACE_BODY = Names.Intern("GENERATED_UINTERFACE_BODY"), GENERATED_BODY_LEGACY = Names.Intern("GENERATED_BODY_LEGACY"),
            Super = Names.Intern("Super"), ThisClass = Names.Intern("ThisClass"), StaticClass = Names.Intern("StaticClass"), StaticStruct = Names.Intern("StaticStruct"),
            TEXT = Names.Intern("TEXT"), DEFINE_FUNCTION = Names.Intern("DEFINE_FUNCTION"), P_THIS = Names.Intern("P_THIS"),
            UClassName = Names.Intern("UClass"), UScriptStructName = Names.Intern("UScriptStruct"), UObjectName = Names.Intern("UObject"),
            FNativeGameplayTag = Names.Intern("FNativeGameplayTag"), FLogCategoryBase = Names.Intern("FLogCategoryBase"),
            Begin = Names.Intern("begin"), OperatorArrow = Names.Intern("operator->"), OperatorStar = Names.Intern("operator*"), OperatorIndex = Names.Intern("operator[]"),
            OperatorCall = Names.Intern("operator()"), Key = Names.Intern("Key"), Value = Names.Intern("Value"), Std = Names.Intern("std");
    }
}
