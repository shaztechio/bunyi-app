// Copyright 2026 Shazron Abdullah and Bunyi contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

import CryptoKit
import Darwin
import Foundation

struct ServerEndpoint: Sendable {
    let directory: URL
    var socket: URL { directory.appendingPathComponent("server.sock") }
    var lock: URL { directory.appendingPathComponent("server.lock") }

    init(scope: String? = ProcessInfo.processInfo.environment["BUNYI_SERVER_SCOPE"]) {
        let dataRoot = FileManager.default.urls(
            for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi", isDirectory: true).path
        let seed = "\(geteuid()):\(scope ?? dataRoot)"
        let digest = SHA256.hash(data: Data(seed.utf8))
            .prefix(12).map { String(format: "%02x", $0) }.joined()
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("bunyi-\(digest)", isDirectory: true)
    }

    func prepareDirectory() throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: directory.path) {
            try verifyPrivateDirectory()
        } else {
            try fm.createDirectory(
                at: directory, withIntermediateDirectories: false,
                attributes: [.posixPermissions: 0o700])
            guard Darwin.chmod(directory.path, 0o700) == 0 else {
                throw posixError()
            }
            try verifyPrivateDirectory()
        }
        if isSymbolicLink(lock.path) {
            throw ServerError(
                code: "server_endpoint_unsafe",
                message: "The Bunyi server lock path cannot be a symbolic link.")
        }
    }

    func verifySocketOwner() throws {
        var details = Darwin.stat()
        guard Darwin.lstat(socket.path, &details) == 0 else {
            if errno == ENOENT {
                throw ServerError(
                    code: "server_unavailable",
                    message: "No Bunyi server is available. Run bunyi server start first.")
            }
            throw posixError()
        }
        guard (details.st_mode & S_IFMT) == S_IFSOCK,
              details.st_uid == geteuid(),
              (details.st_mode & 0o077) == 0 else {
            throw ServerError(
                code: "server_endpoint_unsafe",
                message: "The Bunyi server socket is not private to the current user.")
        }
    }

    private func verifyPrivateDirectory() throws {
        var details = Darwin.stat()
        guard Darwin.lstat(directory.path, &details) == 0,
              (details.st_mode & S_IFMT) == S_IFDIR,
              details.st_uid == geteuid(),
              (details.st_mode & 0o077) == 0 else {
            throw ServerError(
                code: "server_endpoint_unsafe",
                message: "The Bunyi server directory must be private to the current user.")
        }
    }

    private func isSymbolicLink(_ path: String) -> Bool {
        var details = Darwin.stat()
        return Darwin.lstat(path, &details) == 0
            && (details.st_mode & S_IFMT) == S_IFLNK
    }
}

final class ServerInstanceLease {
    private let descriptor: Int32

    init(endpoint: ServerEndpoint) throws {
        let fd = Darwin.open(
            endpoint.lock.path,
            O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW,
            S_IRUSR | S_IWUSR)
        guard fd >= 0 else { throw posixError() }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            let failure = errno
            Darwin.close(fd)
            if failure == EWOULDBLOCK || failure == EAGAIN {
                throw ServerError(
                    code: "bunyi_busy",
                    message: "A Bunyi server is already running or starting.")
            }
            throw POSIXError(POSIXErrorCode(rawValue: failure) ?? .EIO)
        }
        descriptor = fd
        _ = Darwin.fchmod(fd, S_IRUSR | S_IWUSR)
    }

    deinit {
        _ = flock(descriptor, LOCK_UN)
        Darwin.close(descriptor)
    }
}

enum ServerSocket {
    static func listen(at endpoint: ServerEndpoint) throws -> Int32 {
        try endpoint.prepareDirectory()
        _ = Darwin.unlink(endpoint.socket.path)
        let descriptor = try make()
        do {
            try withAddress(endpoint.socket.path) { address, length in
                guard Darwin.bind(descriptor, address, length) == 0 else {
                    throw posixError()
                }
            }
            guard Darwin.chmod(endpoint.socket.path, 0o600) == 0 else {
                throw posixError()
            }
            guard Darwin.listen(descriptor, 32) == 0 else {
                throw posixError()
            }
            return descriptor
        } catch {
            Darwin.close(descriptor)
            _ = Darwin.unlink(endpoint.socket.path)
            throw error
        }
    }

    static func connect(to endpoint: ServerEndpoint) throws -> Int32 {
        try endpoint.prepareDirectory()
        try endpoint.verifySocketOwner()
        let descriptor = try make()
        do {
            try withAddress(endpoint.socket.path) { address, length in
                guard Darwin.connect(descriptor, address, length) == 0 else {
                    throw posixError()
                }
            }
            try verifyPeer(descriptor)
            return descriptor
        } catch {
            Darwin.close(descriptor)
            throw error
        }
    }

    static func accept(from listener: Int32) throws -> Int32 {
        while true {
            let descriptor = Darwin.accept(listener, nil, nil)
            if descriptor >= 0 {
                try configure(descriptor)
                do {
                    try verifyPeer(descriptor)
                    return descriptor
                } catch {
                    Darwin.close(descriptor)
                    throw error
                }
            }
            if errno == EINTR { continue }
            throw posixError()
        }
    }

    static func close(_ descriptor: Int32) {
        _ = Darwin.shutdown(descriptor, SHUT_RDWR)
        Darwin.close(descriptor)
    }

    static func isDisconnected(_ descriptor: Int32) -> Bool {
        var item = pollfd(fd: descriptor, events: Int16(POLLIN), revents: 0)
        let ready = Darwin.poll(&item, 1, 0)
        guard ready > 0 else { return false }
        let terminalEvents = Int16(POLLHUP | POLLERR | POLLNVAL)
        if item.revents & terminalEvents != 0 { return true }
        guard item.revents & Int16(POLLIN) != 0 else { return false }
        var byte: UInt8 = 0
        let count = Darwin.recv(
            descriptor, &byte, 1, MSG_PEEK | MSG_DONTWAIT)
        return count == 0
    }

    private static func make() throws -> Int32 {
        let descriptor = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw posixError() }
        do {
            try configure(descriptor)
            return descriptor
        } catch {
            Darwin.close(descriptor)
            throw error
        }
    }

    private static func configure(_ descriptor: Int32) throws {
        var enabled: Int32 = 1
        guard setsockopt(
            descriptor, SOL_SOCKET, SO_NOSIGPIPE,
            &enabled, socklen_t(MemoryLayout.size(ofValue: enabled))) == 0 else {
            throw posixError()
        }
        _ = fcntl(descriptor, F_SETFD, FD_CLOEXEC)
    }

    private static func verifyPeer(_ descriptor: Int32) throws {
        var user: uid_t = 0
        var group: gid_t = 0
        guard getpeereid(descriptor, &user, &group) == 0,
              user == geteuid() else {
            throw ServerError(
                code: "server_unavailable",
                message: "The Bunyi server peer belongs to another user.")
        }
    }

    private static func withAddress<T>(
        _ path: String,
        _ body: (UnsafePointer<sockaddr>, socklen_t) throws -> T
    ) throws -> T {
        let bytes = Array(path.utf8) + [0]
        var address = sockaddr_un()
        let offset = MemoryLayout.offset(of: \sockaddr_un.sun_path)!
        let length = offset + bytes.count
        guard length <= MemoryLayout<sockaddr_un>.size,
              bytes.count <= MemoryLayout.size(ofValue: address.sun_path) else {
            throw ServerError(
                code: "server_unavailable",
                message: "The Bunyi server socket path is too long.")
        }
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(length)
        withUnsafeMutableBytes(of: &address.sun_path) {
            $0.copyBytes(from: bytes)
        }
        return try withUnsafePointer(to: &address) {
            try $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                try body($0, socklen_t(length))
            }
        }
    }
}

private func posixError() -> POSIXError {
    POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
}
