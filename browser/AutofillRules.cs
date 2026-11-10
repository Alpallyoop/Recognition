using System;
using System.Collections.Generic;

namespace Recognition.Browser
{
    // Pure decisions behind autofill and the save-password offer (proposal 0010). No WPF, no WebView2: executed by browser.tests.
    // The safety rules live here so they are tested directly:
    //   * Autofill happens only on an exact-origin https page (PasswordRules.MayFill), only when exactly one saved login matches
    //     (never a guess between several), never when the vault is locked, and never from SoteriaVault (which asks you per fill).
    //     The form is filled, never submitted (FillScript).
    //   * The save offer is made only for an https top-frame origin the page itself is on, only for a non-empty password, never
    //     in a private tab, and never for a site you said "never" to. A password that is already saved for that username makes
    //     no offer; a changed one offers an update.
    internal enum SaveKind { None, New, Update, Duplicate, Blocked }

    internal static class AutofillRules
    {
        /// <summary>Whether to autofill the one matching saved login into the page now.</summary>
        public static bool ShouldAutofill(bool enabled, bool soteriaPasswordManager, bool mayFill, int exactMatches, bool pinSet, bool unlocked)
            => enabled && !soteriaPasswordManager && mayFill && exactMatches == 1 && (!pinSet || unlocked);

        /// <summary>
        /// What to do with a login the page just submitted. <paramref name="existing"/> is the saved logins for the page's origin.
        /// </summary>
        public static SaveKind ClassifySave(bool enabled, bool isPrivate, bool neverForThisSite, bool https, string? origin,
            string username, string? password, IReadOnlyList<(string Username, string Password)> existing)
        {
            if (!enabled || isPrivate || !https || string.IsNullOrEmpty(origin)) return SaveKind.None;
            if (neverForThisSite) return SaveKind.Blocked;
            if (string.IsNullOrEmpty(password)) return SaveKind.None;
            foreach (var e in existing)
                if (string.Equals(e.Username, username, StringComparison.Ordinal))
                    return e.Password == password ? SaveKind.Duplicate : SaveKind.Update;
            return SaveKind.New;
        }

        /// <summary>The sentence shown on the save bar for each offer kind.</summary>
        public static string SaveBarText(SaveKind kind, string origin, string username)
        {
            var who = string.IsNullOrEmpty(username) ? "" : " for " + username;
            return kind switch
            {
                SaveKind.New => "Save this login" + who + " for " + origin + "?",
                SaveKind.Update => "Update the saved password" + who + " for " + origin + "?",
                _ => "",
            };
        }
    }
}
