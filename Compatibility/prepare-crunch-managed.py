"""Keep Crunch's borrowed source pointer pinned until its native context ends."""
from pathlib import Path
import sys

source, destination = map(Path, sys.argv[1:])
text = source.read_text()
# The public CRN header has one byte each for levels and faces, and its
# first level offset starts at byte 70. Validate against real CRN samples.
assert 'BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(0x10))' in text
text = text.replace('BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(0x10))', 'source[0x10]')
assert 'source.AsSpan(0x47)' in text
text = text.replace('source.AsSpan(0x47)', 'source.AsSpan(0x46)')
old = '        private void* _handle;'
assert text.count(old) == 1
text = text.replace(old, old + '\n        private GCHandle _sourcePin;')
old = '            fixed (byte* sourcePtr = source)'
assert text.count(old) == 2
text = text.replace(old, '            _sourcePin = GCHandle.Alloc(source, GCHandleType.Pinned);\n' + old, 1)
old = '            if (_handle == null)\n                throw new ParserException("Failed to initialize Crunch decompression");'
assert text.count(old) == 1
text = text.replace(old, '            if (_handle == null)\n            {\n                _sourcePin.Free();\n                throw new ParserException("Failed to initialize Crunch decompression");\n            }')
old = '            UnpackEnd(_handle);\n            _handle = null;'
assert text.count(old) == 1
text = text.replace(old, '            try { UnpackEnd(_handle); }\n            finally { _handle = null; if (_sourcePin.IsAllocated) _sourcePin.Free(); }')
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(text)
