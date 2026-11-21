# Security policy

## Supported versions

Only the newest release gets security fixes. Paneless is in beta.

| Version | Supported |
|---|---|
| Newest release (1.0 beta) | ✅ |
| Older betas | ❌ |

## Reporting a vulnerability

**Please don't open a public issue for security problems.** Use GitHub's private reporting instead: on this repository, go to **Security › Report a vulnerability**.

Include what's affected, how to reproduce it, and what an attacker could do with it. You'll get an answer within a week. Once a fix is released, you're credited in the changelog (unless you'd rather not be).

## What Paneless protects, and what it doesn't

- **Unlock codes** are encrypted for your Windows account with DPAPI (on macOS the keychain, on Linux the Secret Service). Anyone who can run programs as your Windows user can decrypt them, as with saved browser passwords.
- A code is only typed after Paneless confirms the device is locked with its lock screen in front. One failed try pauses auto-unlock for that device.
- **Wireless debugging** (adb) gives full control of a device to every computer it's paired with. This is Android's design, not Paneless's. Pair only computers you trust, and remove old pairings in *Developer options › Wireless debugging › Paired devices*.
- Paneless sends nothing to the internet. The one exception is update checks against a source you set yourself.
- Scripts run adb commands with the device permissions adb has. Built-in scripts that change a lot ask before running. Review scripts from others before you run them.
