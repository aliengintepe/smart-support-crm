"""Generate Constant-logits ONNX test graphs, not trained models. Uses protobuf wire format."""
from pathlib import Path
import struct

def varint(value):
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)

def number(field, value):
    return varint(field << 3) + varint(value)

def blob(field, value):
    if isinstance(value, str):
        value = value.encode()
    return varint((field << 3) | 2) + varint(len(value)) + value

def value_info(name, length, dtype):
    dim = number(1, length) if isinstance(length, int) else blob(2, length)
    shape = blob(1, number(1, 1)) + blob(1, dim)
    tensor_type = number(1, dtype) + blob(2, shape)
    return blob(1, name) + blob(2, blob(1, tensor_type))

for length in (8, "sequence"):
    tensor = number(1, 1) + number(1, 3) + number(2, 1) + blob(9, struct.pack('<3f', -3, 1, 4))
    attribute = blob(1, 'value') + blob(5, tensor) + number(20, 4)
    node = blob(2, 'logits') + blob(4, 'Constant') + blob(5, attribute)
    graph = blob(1, node) + blob(2, 'test-only-constant-logits')
    for name in ('input_ids', 'attention_mask', 'token_type_ids'):
        graph += blob(11, value_info(name, length, 7))
    graph += blob(12, value_info('logits', 3, 1))
    model = number(1, 8) + blob(2, 'SmartSupportCRM regression fixture') + blob(6, 'TEST ONLY. ' * 150)
    model += blob(7, graph) + blob(8, number(2, 13))
    Path(__file__).with_name(f'constant-{length}.onnx').write_bytes(model)
