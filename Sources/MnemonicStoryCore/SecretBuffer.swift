import Darwin
import Foundation

/// Owns exclusive pages; clear invalidates all aliases. UI/system copies are outside this guarantee.
public final class SecretBuffer: @unchecked Sendable {
    public let count: Int
    private let pointer: UnsafeMutableRawPointer
    private let allocationCount: Int
    private let locked: Bool
    private let lock = NSLock()
    private var cleared = false

    public init(count: Int) {
        precondition((0...65_536).contains(count))
        self.count = count
        let page = Int(getpagesize())
        allocationCount = max(1, (count + page - 1) / page) * page
        guard let p = mmap(nil, allocationCount, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0),
              p != UnsafeMutableRawPointer(bitPattern: -1) else { preconditionFailure("Memory allocation failed") }
        pointer = p
        locked = mlock(p, allocationCount) == 0
    }

    public convenience init(_ text: String) {
        self.init(count: text.utf8.count)
        write { bytes in
            for (i, byte) in text.utf8.enumerated() { bytes[i] = byte }
        }
    }

    public var isCleared: Bool { lock.withLock { cleared } }
    public var pagesLocked: Bool { locked }
    public func read<T>(_ body: (UnsafeRawBufferPointer) throws -> T) rethrows -> T? {
        try lock.withLock {
            guard !cleared else { return nil }
            return try body(UnsafeRawBufferPointer(start: pointer, count: count))
        }
    }
    @discardableResult public func write<T>(_ body: (UnsafeMutableRawBufferPointer) throws -> T) rethrows -> T? {
        try lock.withLock {
            guard !cleared else { return nil }
            return try body(UnsafeMutableRawBufferPointer(start: pointer, count: count))
        }
    }
    public func displayText() -> String { read { String(decoding: $0, as: UTF8.self) } ?? "" }
    public func clear() {
        lock.withLock { _ = memset_s(pointer, allocationCount, 0, allocationCount); cleared = true }
    }
    /// Used by tests to inspect a still-allocated cleared buffer without materializing Strings.
    public var containsOnlyZeroBytes: Bool {
        lock.withLock { UnsafeRawBufferPointer(start: pointer, count: allocationCount).allSatisfy { $0 == 0 } }
    }
    deinit { clear(); if locked { _ = munlock(pointer, allocationCount) }; _ = munmap(pointer, allocationCount) }
}
