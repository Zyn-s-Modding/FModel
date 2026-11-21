from pathlib import Path
import sys

source, destination = map(Path, sys.argv[1:])
text = source.read_text()
assert text.count('#ifdef __x86_64__') == 1
text = text.replace('#ifdef __x86_64__', '#if defined(__x86_64__) || defined(__aarch64__)')
text = text.replace('(uint32)p_new', 'reinterpret_cast<ptr_bits>(p_new)')
text = text.replace('(uint32)p ', 'reinterpret_cast<ptr_bits>(p) ')
destination.write_text(text)
