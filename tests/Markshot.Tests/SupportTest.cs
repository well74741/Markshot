using System;
using System.Text.Json;
using Markshot.Core;

static class SupportTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    public static void Run()
    {
        const string key = "test-key-0123456789abcdef";
        var stored = Secrets.Protect(key);
        Check(stored.StartsWith("dpapi:") && !stored.Contains(key) && Secrets.Unprotect(stored) == key, "API key is encrypted and decrypts for this user");
        Check(Secrets.Unprotect("dpapi:AAAA") == "", "a key from another PC / user reads as empty (re-enter)");

        var json = JsonSerializer.Serialize(new TranslationConfig { ApiKey = key });
        Check(!json.Contains(key) && json.Contains("ApiKeyProtected") && !json.Contains("\"ApiKey\""), "config file holds only the encrypted key");
        var back = JsonSerializer.Deserialize<TranslationConfig>(json)!;
        Check(back.ApiKey == key, "encrypted key loads back");
        var legacy = JsonSerializer.Deserialize<TranslationConfig>("{\"ApiKey\":\"" + key + "\"}")!;
        Check(legacy.ApiKey == key, "an old plain-text key is still read (and saved encrypted next time)");

        var redacted = Diagnostics.Redact("{\"Translation\":{\"BaseUrl\":\"https://x\",\"ApiKeyProtected\":\"" + stored + "\",\"ApiKey\":\"" + key + "\"}}");
        Check(!redacted.Contains(key) && !redacted.Contains(stored) && redacted.Contains("https://x"), "diagnostics config has no key");
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
