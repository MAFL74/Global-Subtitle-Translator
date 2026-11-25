"""Convert one local safetensors checkpoint to FP16 without loading all weights at once."""

from __future__ import annotations

import argparse
import json
import os
import struct
import tempfile
from pathlib import Path

import numpy as np
from safetensors import safe_open


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="Input .safetensors file")
    parser.add_argument("output", type=Path, help="Output FP16 .safetensors file")
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()

    source_path = args.input.expanduser().resolve()
    output_path = args.output.expanduser().resolve()
    if not source_path.is_file():
        parser.error(f"Input does not exist: {source_path}")
    if source_path == output_path:
        parser.error("Input and output must be different files")
    if output_path.exists() and not args.overwrite:
        parser.error(f"Output exists; use --overwrite to replace it: {output_path}")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with safe_open(source_path, framework="np", device="cpu") as source:
            keys = source.keys()
            header: dict[str, object] = {}
            offset = 0
            for key in keys:
                tensor_slice = source.get_slice(key)
                shape = tensor_slice.get_shape()
                original_dtype = tensor_slice.get_dtype()
                if original_dtype != "F32":
                    raise RuntimeError(f"Expected F32 input weights, found {original_dtype} in {key}")
                element_count = int(np.prod(shape, dtype=np.int64))
                byte_count = element_count * 2
                header[key] = {
                    "dtype": "F16",
                    "shape": shape,
                    "data_offsets": [offset, offset + byte_count],
                }
                offset += byte_count
            header["__metadata__"] = {"format": "pt", "converted_dtype": "float16"}
            encoded_header = json.dumps(header, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            encoded_header += b" " * ((-len(encoded_header)) % 8)

            with tempfile.NamedTemporaryFile(
                dir=output_path.parent,
                prefix=f".{output_path.name}.",
                suffix=".tmp",
                delete=False,
            ) as output:
                temporary_path = Path(output.name)
                output.write(struct.pack("<Q", len(encoded_header)))
                output.write(encoded_header)
                for index, key in enumerate(keys, start=1):
                    tensor = source.get_tensor(key)
                    half = np.ascontiguousarray(tensor.astype(np.float16))
                    output.write(memoryview(half).cast("B"))
                    del tensor, half
                    if index % 100 == 0 or index == len(keys):
                        print(f"Converted {index}/{len(keys)} tensors", flush=True)
                output.flush()
                os.fsync(output.fileno())

        # Validate the safetensors header without memory-mapping the full output file.
        with temporary_path.open("rb") as converted:
            header_size_bytes = converted.read(8)
            if len(header_size_bytes) != 8:
                raise RuntimeError("Converted checkpoint has a truncated header")
            header_size = struct.unpack("<Q", header_size_bytes)[0]
            header = json.loads(converted.read(header_size).decode("utf-8"))
            tensor_header = {key: value for key, value in header.items() if key != "__metadata__"}
            if list(tensor_header) != keys:
                raise RuntimeError("Converted checkpoint has a different tensor list")
            if any(info.get("dtype") != "F16" for info in tensor_header.values()):
                raise RuntimeError("Converted checkpoint contains a non-FP16 tensor")
            data_size = max((info["data_offsets"][1] for info in tensor_header.values()), default=0)
            if temporary_path.stat().st_size != 8 + header_size + data_size:
                raise RuntimeError("Converted checkpoint size does not match its tensor offsets")
        os.replace(temporary_path, output_path)
        temporary_path = None
        print(f"FP16 checkpoint saved: {output_path} ({output_path.stat().st_size:,} bytes)")
        return 0
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


if __name__ == "__main__":
    raise SystemExit(main())
