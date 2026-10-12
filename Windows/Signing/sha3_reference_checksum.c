/* Public-file adapter for the pinned, independent pq-crystals FIPS202 C reference. */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include "fips202.h"

int main(int argc, char **argv) {
    if (argc != 2) return 64;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 66;
    if (_fseeki64(file, 0, SEEK_END)) { fclose(file); return 74; }
    const __int64 length = _ftelli64(file);
    if (length < 0 || length > 2147483647 || _fseeki64(file, 0, SEEK_SET)) { fclose(file); return 74; }
    unsigned char *bytes = (unsigned char *)malloc(length ? (size_t)length : 1);
    if (!bytes) { fclose(file); return 70; }
    if (fread(bytes, 1, (size_t)length, file) != (size_t)length || ferror(file) || fclose(file)) { free(bytes); return 74; }
    unsigned char digest[64];
    sha3_512(digest, bytes, (size_t)length);
    free(bytes);
    for (size_t i = 0; i < sizeof(digest); ++i) printf("%02x", digest[i]);
    return fflush(stdout) ? 74 : 0;
}
