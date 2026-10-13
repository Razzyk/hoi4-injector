#ifndef HOI4_ADMISSION_PROTOCOL_H
#define HOI4_ADMISSION_PROTOCOL_H
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define ADMISSION_MAGIC 0x41444731u
#define ADMISSION_PROTOCOL 2u
#define GAME_SHA256 "7dc947be34970da1e1c787bcddbe7f62b610ff258aebf8f06aa8b348f3a031d5"

typedef struct AdmissionConfig {
    uint32_t magic, bytes, protocol, heartbeat_ms;
    char session[64], player[64];
    unsigned char secret[32];
    WCHAR pipe_path[256], manifest_path[1024], log_path[1024];
} AdmissionConfig;
_Static_assert(sizeof(AdmissionConfig) == 4784, "Bootstrap layout mismatch");

static inline void hex_encode(const unsigned char *bytes, size_t length, char *out) {
    const char *digits = "0123456789abcdef";
    for (size_t i = 0; i < length; ++i) { out[i * 2] = digits[bytes[i] >> 4]; out[i * 2 + 1] = digits[bytes[i] & 15]; }
    out[length * 2] = 0;
}
static inline int hex_decode(const char *text, unsigned char *out, size_t bytes) {
    if (strlen(text) != bytes * 2) return 0;
    for (size_t i = 0; i < bytes; ++i) {
        unsigned v = 0;
        for (size_t j = 0; j < 2; ++j) {
            char c = text[i * 2 + j];
            unsigned d = c >= '0' && c <= '9' ? (unsigned)(c - '0') : c >= 'a' && c <= 'f' ? (unsigned)(c - 'a' + 10) : c >= 'A' && c <= 'F' ? (unsigned)(c - 'A' + 10) : 99;
            if (d > 15) return 0;
            v = v * 16 + d;
        }
        out[i] = (unsigned char)v;
    }
    return 1;
}
static inline int sha_file(const WCHAR *path, char out[65]) {
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    unsigned char digest[32], buffer[65536];
    DWORD read = 0;
    int ok = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto done;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0) goto done;
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) goto done;
    for (;;) {
        if (!ReadFile(file, buffer, sizeof(buffer), &read, NULL)) goto done;
        if (!read) break;
        if (BCryptHashData(hash, buffer, read, 0) < 0) goto done;
    }
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    hex_encode(digest, sizeof(digest), out);
    ok = 1;
done:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}
static inline int hmac_message(const unsigned char secret[32], const char *message, char out[65]) {
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    unsigned char digest[32];
    int ok = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, BCRYPT_ALG_HANDLE_HMAC_FLAG) < 0) goto done;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, (PUCHAR)secret, 32, 0) < 0) goto done;
    if (BCryptHashData(hash, (PUCHAR)message, (ULONG)strlen(message), 0) < 0) goto done;
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    hex_encode(digest, sizeof(digest), out);
    ok = 1;
done:
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}
static inline int sha_bytes(const void *data, size_t length, char out[65]) {
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    unsigned char digest[32];
    int ok = 0;
    if (length > 0xffffffffu || BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto done;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0) goto done;
    if (BCryptHashData(hash, (PUCHAR)data, (ULONG)length, 0) < 0 || BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    hex_encode(digest, sizeof(digest), out);
    ok = 1;
done:
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}
#endif
