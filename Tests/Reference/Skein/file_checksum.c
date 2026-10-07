#include <stdio.h>

#include "skein.h"

/* File-I/O adapter only; all hashing uses the unmodified Skein 1.3 reference. */
int main(int argc, char **argv)
{
    if (argc != 2) {
        fputs("Usage: skein-reference-checksum <file>\n", stderr);
        return 64;
    }

    FILE *input = fopen(argv[1], "rb");
    if (input == NULL) {
        perror("skein-reference-checksum: cannot open input");
        return 66;
    }

    Skein1024_Ctxt_t context;
    unsigned char buffer[65536];
    unsigned char digest[SKEIN1024_STATE_BYTES];
    char hexadecimal[SKEIN1024_STATE_BYTES * 2];
    const char alphabet[] = "0123456789abcdef";

    if (Skein1024_Init(&context, 1024) != SKEIN_SUCCESS) {
        fputs("skein-reference-checksum: initialization failed\n", stderr);
        fclose(input);
        return 70;
    }

    size_t count;
    while ((count = fread(buffer, 1, sizeof buffer, input)) != 0) {
        if (Skein1024_Update(&context, buffer, count) != SKEIN_SUCCESS) {
            fputs("skein-reference-checksum: update failed\n", stderr);
            fclose(input);
            return 70;
        }
    }
    if (ferror(input)) {
        perror("skein-reference-checksum: cannot read input");
        fclose(input);
        return 74;
    }
    if (fclose(input) != 0) {
        perror("skein-reference-checksum: cannot close input");
        return 74;
    }
    if (Skein1024_Final(&context, digest) != SKEIN_SUCCESS) {
        fputs("skein-reference-checksum: finalization failed\n", stderr);
        return 70;
    }

    for (size_t index = 0; index < sizeof digest; ++index) {
        hexadecimal[index * 2] = alphabet[digest[index] >> 4];
        hexadecimal[index * 2 + 1] = alphabet[digest[index] & 15];
    }
    if (fwrite(hexadecimal, 1, sizeof hexadecimal, stdout) != sizeof hexadecimal
        || fflush(stdout) != 0) {
        fputs("skein-reference-checksum: cannot write digest\n", stderr);
        return 74;
    }
    return 0;
}
