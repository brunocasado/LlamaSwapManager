using System;
using TestVersion;

class Program
{
    static int _passed = 0;
    static int _failed = 0;

    static void Main()
    {
        Console.WriteLine("=== LlamaSwapManager Version Check Tests ===\n");

        TestVersionParsing();
        TestVersionComparison();
        TestHasUpdate();
        TestStripPrefix();
        TestEdgeCases();

        Console.WriteLine($"\n=== Results: {_passed} passed, {_failed} failed ===");
        Environment.Exit(_failed > 0 ? 1 : 0);
    }

    static void Assert(bool condition, string testName)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS: {testName}");
            _passed++;
        }
        else
        {
            Console.WriteLine($"  FAIL: {testName}");
            _failed++;
        }
    }

    static void AssertEqual<T>(T expected, T actual, string testName)
    {
        Assert(expected!.Equals(actual), $"{testName} (expected {expected}, got {actual})");
    }

    static void TestVersionParsing()
    {
        Console.WriteLine("\n--- Version Parsing ---");

        var v1 = VersionComparer.Parse("v224");
        Assert(v1 is not null, "Parse 'v224' succeeds");
        AssertEqual(VersionType.LlamaSwap, v1!.Type, "v224 type is LlamaSwap");
        AssertEqual((ulong)224, v1.NumericValue, "v224 value is 224");

        var v2 = VersionComparer.Parse("b9616");
        Assert(v2 is not null, "Parse 'b9616' succeeds");
        AssertEqual(VersionType.LlamaCpp, v2!.Type, "b9616 type is LlamaCpp");
        AssertEqual((ulong)0x9616, v2.NumericValue, "b9616 value is 0x9616");

        var v3 = VersionComparer.Parse("v1");
        Assert(v3 is not null, "Parse 'v1' succeeds");
        AssertEqual((ulong)1, v3!.NumericValue, "v1 value is 1");

        var v4 = VersionComparer.Parse("b0001");
        Assert(v4 is not null, "Parse 'b0001' succeeds");
        AssertEqual((ulong)1, v4!.NumericValue, "b0001 value is 1");

        var vNull = VersionComparer.Parse(null);
        Assert(vNull is null, "Parse null returns null");

        var vEmpty = VersionComparer.Parse("");
        Assert(vEmpty is null, "Parse empty returns null");

        var vBad = VersionComparer.Parse("invalid");
        Assert(vBad is null, "Parse 'invalid' returns null");

        var vSpaces = VersionComparer.Parse("  v224  ");
        Assert(vSpaces is not null, "Parse '  v224  ' trims whitespace");
        AssertEqual("v224", vSpaces!.Raw, "Parsed raw is trimmed");
    }

    static void TestVersionComparison()
    {
        Console.WriteLine("\n--- Version Comparison ---");

        // Same version
        AssertEqual(0, VersionComparer.Compare("v224", "v224"), "v224 == v224");
        AssertEqual(0, VersionComparer.Compare("b9616", "b9616"), "b9616 == b9616");

        // LlamaSwap: newer
        AssertEqual(1, VersionComparer.Compare("v224", "v223"), "v224 > v223");
        AssertEqual(-1, VersionComparer.Compare("v223", "v224"), "v223 < v224");

        // LlamaSwap: large gap
        AssertEqual(-1, VersionComparer.Compare("v1", "v224"), "v1 < v224");
        AssertEqual(1, VersionComparer.Compare("v224", "v1"), "v224 > v1");

        // LlamaSwap: single digit vs double
        AssertEqual(-1, VersionComparer.Compare("v9", "v10"), "v9 < v10");
        AssertEqual(1, VersionComparer.Compare("v10", "v9"), "v10 > v9");

        // LlamaCpp: newer
        AssertEqual(1, VersionComparer.Compare("b9616", "b9610"), "b9616 > b9610");
        AssertEqual(-1, VersionComparer.Compare("b9610", "b9616"), "b9610 < b9616");

        // LlamaCpp: hex comparison (not decimal)
        AssertEqual(1, VersionComparer.Compare("b9616", "b9615"), "b9616 > b9615 (hex)");

        // Different types: not comparable
        AssertEqual(0, VersionComparer.Compare("v224", "b9616"), "v224 vs b9616 (different types)");
        AssertEqual(0, VersionComparer.Compare("b9616", "v224"), "b9616 vs v224 (different types)");

        // Null handling
        AssertEqual(0, VersionComparer.Compare(null, null), "null == null");
        AssertEqual(-1, VersionComparer.Compare(null, "v224"), "null < v224");
        AssertEqual(1, VersionComparer.Compare("v224", null), "v224 > null");
    }

    static void TestHasUpdate()
    {
        Console.WriteLine("\n--- HasUpdate ---");

        Assert(VersionComparer.HasUpdate("v223", "v224"), "v223 -> v224 has update");
        Assert(!VersionComparer.HasUpdate("v224", "v224"), "v224 -> v224 no update");
        Assert(!VersionComparer.HasUpdate("v224", "v223"), "v224 -> v223 no update");
        Assert(VersionComparer.HasUpdate("v1", "v10"), "v1 -> v10 has update");
        // null current version: Compare returns -1, so HasUpdate returns true (conservative)
        Assert(VersionComparer.HasUpdate(null, "v224"), "null -> v224 returns true (conservative)");
        Assert(!VersionComparer.HasUpdate("v224", null), "v224 -> null no update");
        Assert(!VersionComparer.HasUpdate("v224", "b9616"), "v224 -> b9616 no update (diff type)");
    }

    static void TestStripPrefix()
    {
        Console.WriteLine("\n--- StripPrefix ---");

        AssertEqual("224", VersionComparer.StripPrefix("v224"), "Strip 'v224'");
        AssertEqual("224", VersionComparer.StripPrefix("V224"), "Strip 'V224'");
        AssertEqual("9616", VersionComparer.StripPrefix("b9616"), "Strip 'b9616'");
        AssertEqual("9616", VersionComparer.StripPrefix("B9616"), "Strip 'B9616'");
        AssertEqual("224", VersionComparer.StripPrefix("224"), "No strip '224'");
        AssertEqual("", VersionComparer.StripPrefix(""), "Strip empty");
        AssertEqual("", VersionComparer.StripPrefix(null!), "Strip null");
    }

    static void TestEdgeCases()
    {
        Console.WriteLine("\n--- Edge Cases ---");

        // Very large version numbers
        Assert(VersionComparer.HasUpdate("v1", "v999999"), "v1 -> v999999 has update");
        AssertEqual(-1, VersionComparer.Compare("v1", "v999999"), "v1 < v999999");

        // LlamaCpp boundary: b9999 vs ba000
        AssertEqual(1, VersionComparer.Compare("ba000", "b9999"), "ba000 > b9999 (hex boundary)");
        AssertEqual(-1, VersionComparer.Compare("b9999", "ba000"), "b9999 < ba000 (hex boundary)");

        // Zero versions
        AssertEqual(0, VersionComparer.Compare("v0", "v0"), "v0 == v0");
        AssertEqual(-1, VersionComparer.Compare("v0", "v1"), "v0 < v1");

        // Case insensitivity for prefixes
        var vLower = VersionComparer.Parse("V224");
        Assert(vLower is not null, "Parse 'V224' (uppercase V)");
        AssertEqual((ulong)224, vLower!.NumericValue, "V224 value is 224");

        var bLower = VersionComparer.Parse("B9616");
        Assert(bLower is not null, "Parse 'B9616' (uppercase B)");
        AssertEqual((ulong)0x9616, bLower!.NumericValue, "B9616 value is 0x9616");
    }
}
