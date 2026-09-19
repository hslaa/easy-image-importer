"""Builds the animal-recognition model files the app downloads on first use.

Converts SpeciesNet (Google, Apache-2.0: MegaDetector v5a detector + species classifier) to
ONNX, stores the weights as float16 to halve the download, and writes norway.json with
the labels, taxonomy and Norwegian geofence the app needs to reproduce SpeciesNet's decisions.

    pip install speciesnet onnx onnxruntime onnxscript
    python tools/models/convert.py out/

Then verify with tools/models/verify.py before publishing.
"""
import hashlib
import json
import sys
from pathlib import Path

import numpy as np
import onnx
import torch
from onnx import helper, numpy_helper
from speciesnet import SpeciesNet
from speciesnet.geofence_utils import should_geofence_animal_classification
from speciesnet.taxonomy_utils import get_full_class_string

MODEL = "kaggle:google/speciesnet/pyTorch/v4.0.3a/1"
COUNTRY = "NOR"
OPSET = 17


class DetectorOutput(torch.nn.Module):
    """YOLOv5 returns (predictions, features); only the predictions are needed."""

    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, x):
        return self.model(x, augment=False)[0]


def export_detector(model, path: Path):
    dummy = torch.zeros(1, 3, 1280, 1280)
    torch.onnx.export(
        DetectorOutput(model), dummy, str(path), opset_version=OPSET, dynamo=False,
        input_names=["images"], output_names=["predictions"],
        # Height and width vary: the long side is 1280, the short side a multiple of 64.
        dynamic_axes={"images": {2: "height", 3: "width"}, "predictions": {1: "boxes"}},
    )


def export_classifier(model, path: Path):
    dummy = torch.zeros(1, 480, 480, 3)  # NHWC, values 0..1 (the model's TensorFlow heritage)
    torch.onnx.export(
        model, dummy, str(path), opset_version=OPSET, dynamo=False,
        input_names=["crops"], output_names=["logits"],
        dynamic_axes={"crops": {0: "batch"}, "logits": {0: "batch"}},
    )


def to_fp16(src: Path, dst: Path):
    """Halves the file: weights are stored as float16 and cast back to float32 inside the graph.

    The model still computes in float32 exactly as before; ONNX Runtime folds the casts away when
    it loads the model. (onnxconverter-common's full float16 conversion breaks YOLOv5's shape
    arithmetic, and float16 compute is no faster on an ordinary CPU anyway.)
    """
    model = onnx.load(str(src))
    graph = model.graph
    casts, keep = [], []
    for init in graph.initializer:
        if init.data_type == onnx.TensorProto.FLOAT and np.prod(init.dims) >= 1024:
            half = numpy_helper.from_array(numpy_helper.to_array(init).astype(np.float16), init.name + "_fp16")
            keep.append(half)
            casts.append(helper.make_node("Cast", [half.name], [init.name], to=onnx.TensorProto.FLOAT,
                                          name=init.name + "_to_fp32"))
        else:
            keep.append(init)
    del graph.initializer[:]
    graph.initializer.extend(keep)
    nodes = casts + list(graph.node)
    del graph.node[:]
    graph.node.extend(nodes)
    onnx.save(model, str(dst))


def norway_data(ensemble, classifier_labels: list[str], path: Path):
    taxonomy = ensemble.taxonomy_map  # "class;order;family;genus;species" -> full label
    blocked = sorted(
        key for key, label in taxonomy.items()
        if should_geofence_animal_classification(label, COUNTRY, None, ensemble.geofence_map, True)
    )
    data = {
        "source": MODEL,
        "country": COUNTRY,
        "labels": classifier_labels,                  # classifier output index -> label
        "taxonomy": sorted(taxonomy.values()),        # every label, including higher levels
        "blocked": blocked,                           # full class strings not found in Norway
    }
    path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    return len(blocked)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main(out: Path):
    out.mkdir(parents=True, exist_ok=True)
    net = SpeciesNet(MODEL)
    detector, classifier, ensemble = net.detector, net.classifier, net.ensemble
    for m in (detector.model, classifier.model):
        m.cpu().float().eval()

    export_detector(detector.model, out / "detector.fp32.onnx")
    export_classifier(classifier.model, out / "classifier.fp32.onnx")
    to_fp16(out / "detector.fp32.onnx", out / "detector.onnx")
    to_fp16(out / "classifier.fp32.onnx", out / "classifier.onnx")
    labels = [classifier.labels[i] for i in range(len(classifier.labels))]
    blocked = norway_data(ensemble, labels, out / "norway.json")

    manifest = {name: {"bytes": (out / name).stat().st_size, "sha256": sha256(out / name)}
                for name in ("detector.onnx", "classifier.onnx", "norway.json")}
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(json.dumps(manifest, indent=2))
    print(f"{len(labels)} classifier labels, {blocked} taxa not found in Norway")


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else "out"))
