/* Test driver: replays core requests (one JSON per line, from a file) through the six exported functions and prints
   each answer on its own line. Linked statically with the same objects as libarkham_core.so, so it runs under
   qemu-aarch64 on the build machine; tools/replay-win.py does the same against the Windows arkham_core.dll. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "arkham_core.h"

int main(int argc, char **argv) {
    if (argc != 2) { fprintf(stderr, "usage: core-cli requests.jsonl\n"); return 2; }
    FILE *in = fopen(argv[1], "rb");
    if (!in) { perror("open"); return 2; }
    int rc = ah_core_init();
    if (rc) { fprintf(stderr, "init %d\n", rc); return 1; }
    size_t cap = 1 << 20; char *line = malloc(cap); size_t len = 0; int c;
    for (;;) {
        len = 0;
        while ((c = fgetc(in)) != EOF && c != '\n') { if (len + 1 >= cap) { cap *= 2; line = realloc(line, cap); } line[len++] = (char)c; }
        if (len == 0 && c == EOF) break;
        if (len == 0) continue;
        uint64_t h = 0, size = 0;
        rc = ah_core_call((const uint8_t *)line, len, &h);
        if (rc) { printf("{\"cliError\":\"call %d\"}\n", rc); fflush(stdout); continue; }
        ah_core_result_size(h, &size);
        char *out = malloc(size + 1);
        ah_core_result_copy(h, (uint8_t *)out, size);
        ah_core_result_release(h);
        fwrite(out, 1, size, stdout); fputc('\n', stdout); fflush(stdout);
        free(out);
        if (c == EOF) break;
    }
    return 0;
}
