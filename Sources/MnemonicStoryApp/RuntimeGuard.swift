import Darwin
import Foundation
import Security

enum RuntimeGuard {
    static let team = "2T6K9PGS55"
    static func disableCoreDumps() -> Bool {
        var limit = rlimit(rlim_cur: 0, rlim_max: 0)
        guard setrlimit(RLIMIT_CORE, &limit) == 0 else { return false }
        var actual = rlimit()
        return getrlimit(RLIMIT_CORE, &actual) == 0 && actual.rlim_cur == 0 && actual.rlim_max == 0
    }
    static func current() -> Bool {
        guard disableCoreDumps() else { return false }
        var dynamic: SecCode?
        guard SecCodeCopySelf([], &dynamic) == errSecSuccess, let dynamic else { return false }
        return validateDynamic(dynamic, identifier: "local.passphrasereminder.reminder")
    }
    static func validateRunningRunner(_ pid: pid_t) -> Bool {
        guard pid > 0 else { return false }
        let attributes = [kSecGuestAttributePid as String: NSNumber(value: pid)] as CFDictionary
        var dynamic: SecCode?
        guard SecCodeCopyGuestWithAttributes(nil, attributes, [], &dynamic) == errSecSuccess,
              let dynamic else { return false }
        return validateDynamic(dynamic, identifier: "local.passphrasereminder.reminder.runner")
    }
    private static func validateDynamic(_ dynamic: SecCode, identifier: String) -> Bool {
        guard SecCodeCheckValidity(dynamic, [], nil) == errSecSuccess else { return false }
        let bridge: SecStaticCode = unsafeBitCast(dynamic, to: SecStaticCode.self)
        var info: CFDictionary?
        // Dynamic status alone omits the signing team. Request both categories
        // so an otherwise valid Developer ID process can pass the team check.
        guard SecCodeCopySigningInformation(bridge, SecCSFlags(rawValue: kSecCSDynamicInformation | kSecCSSigningInformation), &info) == errSecSuccess,
              let dictionary = info as NSDictionary? else { return false }
        let flags = SecCodeSignatureFlags(rawValue: (dictionary[kSecCodeInfoFlags] as? NSNumber)?.uint32Value ?? 0)
        let status = SecCodeStatus(rawValue: (dictionary[kSecCodeInfoStatus] as? NSNumber)?.uint32Value ?? 0)
        let entitlements = dictionary[kSecCodeInfoEntitlementsDict] as? [String: Any] ?? [:]
        return dictionary[kSecCodeInfoTeamIdentifier] as? String == team
            && dictionary[kSecCodeInfoIdentifier] as? String == identifier
            && flags.contains(.runtime) && status.contains(.valid) && status.contains(.hard)
            && status.contains(.kill) && !status.contains(.debugged)
            && allowedEntitlements(entitlements)
    }
    static func validateRunner(_ url: URL) -> Bool {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code,
              SecStaticCodeCheckValidity(code, SecCSFlags(rawValue: kSecCSCheckAllArchitectures), nil) == errSecSuccess else { return false }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let dictionary = info as NSDictionary? else { return false }
        let flags = SecCodeSignatureFlags(rawValue: (dictionary[kSecCodeInfoFlags] as? NSNumber)?.uint32Value ?? 0)
        let entitlements = dictionary[kSecCodeInfoEntitlementsDict] as? [String: Any] ?? [:]
        return dictionary[kSecCodeInfoTeamIdentifier] as? String == team
            && dictionary[kSecCodeInfoIdentifier] as? String == "local.passphrasereminder.reminder.runner" && flags.contains(.runtime)
            && allowedEntitlements(entitlements)
    }
    private static func allowedEntitlements(_ value: [String: Any]) -> Bool {
        let allowed = Set(["com.apple.application-identifier", "com.apple.developer.team-identifier"])
        return Set(value.keys).isSubset(of: allowed)
    }
}
