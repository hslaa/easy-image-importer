"""Checks that the ONNX models give the same answers as SpeciesNet itself.

Runs SpeciesNet's own preprocessing and decision logic, swapping only the two PyTorch models
for the ONNX files, over a folder of images, and compares with a reference predictions JSON
made by `python -m speciesnet.scripts.run_model` on the same images and country.

    python tools/models/verify.py <models dir> <image folder> <reference.json | -> <country> [admin1] [--fp32]

With "-" instead of a reference, it only writes the golden file (e.g. for country NOR, which the
app uses, while the PyTorch reference run used the photos' real country).

Also writes <models dir>/golden-<folder name>.json: per image, the detections and the final
prediction, which the app's C# port is tested against.
"""
import json
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from PIL import Image
from speciesnet import SpeciesNet
from yolov5.utils.general import non_max_suppression, scale_boxes, xyxy2xywhn

MODEL = "kaggle:google/speciesnet/pyTorch/v4.0.3a/1"


def main(models: Path, folder: Path, reference: Path, country: str, admin1: str | None, fp32: bool):
    suffix = ".fp32.onnx" if fp32 else ".onnx"
    detector = ort.InferenceSession(str(models / f"detector{suffix}"), providers=["CPUExecutionProvider"])
    classifier = ort.InferenceSession(str(models / f"classifier{suffix}"), providers=["CPUExecutionProvider"])
    net = SpeciesNet(MODEL)
    ref = {} if str(reference) == "-" else {
        Path(p["filepath"]).name + p["filepath"].split("DCIM")[-1]: p for p in json.load(open(reference))["predictions"]}

    golden, same, close, differ, det_diffs = [], 0, 0, 0, []
    for path in sorted(folder.rglob("*.JPG")):
        if path.name.startswith("._"):
            continue
        img = Image.open(path).convert("RGB")

        pre = net.detector.preprocess(img)
        x = (pre.arr / 255).astype(np.float32).transpose(2, 0, 1)[None]
        raw = torch.from_numpy(detector.run(None, {"images": x})[0])
        boxes = non_max_suppression(raw, conf_thres=net.detector.DETECTION_THRESHOLD)[0]
        detections = []
        if len(boxes):
            boxes[:, :4] = scale_boxes(x.shape[2:], boxes[:, :4], (pre.orig_height, pre.orig_width)).round()
            for b in boxes:
                cx, cy, w, h = xyxy2xywhn(b[None, :4], w=pre.orig_width, h=pre.orig_height)[0].tolist()
                label = {1: "animal", 2: "human", 3: "vehicle"}[int(b[5]) + 1]
                detections.append({"category": str(int(b[5]) + 1), "label": label, "conf": float(b[4]),
                                   "bbox": [cx - w / 2, cy - h / 2, w, h]})
        detections.sort(key=lambda d: d["conf"], reverse=True)

        from speciesnet.utils import BBox
        crop = net.classifier.preprocess(img, bboxes=[BBox(*detections[0]["bbox"])] if detections else None)
        logits = classifier.run(None, {"crops": (crop.arr / 255).astype(np.float32)[None]})[0]
        scores = torch.softmax(torch.from_numpy(logits), dim=-1)
        top, idx = torch.topk(scores, k=5, dim=-1)
        classifications = {"classes": [net.classifier.labels[i] for i in idx[0].tolist()], "scores": top[0].tolist()}

        label, score, source = net.ensemble.combine(
            filepaths=[str(path)], classifier_results={str(path): {"filepath": str(path), "classifications": classifications}},
            detector_results={str(path): {"filepath": str(path), "detections": detections}},
            geolocation_results={str(path): {"country": country, "admin1_region": admin1}},
            partial_predictions={},
        )[0]["prediction"], None, None
        key = path.name + str(path).split("DCIM")[-1]
        golden.append({"path": str(path.relative_to(folder)), "detections": detections[:5],
                       "classes": classifications["classes"], "scores": classifications["scores"],
                       "prediction": label})
        if key not in ref:
            continue
        r = ref[key]
        if label == r["prediction"]:
            same += 1
        elif label.split(";")[1:4] == r["prediction"].split(";")[1:4]:
            close += 1  # same class/order/family
        else:
            differ += 1
            print(f"  differs: {path.name}: onnx {label.split(';')[-1]} vs torch {r['prediction'].split(';')[-1]}")
        top_ref = max((d["conf"] for d in r.get("detections", []) if d["label"] == "animal"), default=0)
        top_onnx = max((d["conf"] for d in detections if d["label"] == "animal"), default=0)
        det_diffs.append(abs(top_ref - top_onnx))

    n = same + close + differ
    if n: print(f"{folder.parent.name}: {n} images. Final prediction identical {same}, same family {close}, different {differ}. "
          f"Top animal confidence differs by max {max(det_diffs):.3f}, mean {np.mean(det_diffs):.4f}.")
    if not fp32:
        (models / f"golden-{folder.parent.name}-{country}.json").write_text(json.dumps(golden, ensure_ascii=False))


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--fp32"]
    main(Path(args[0]), Path(args[1]), Path(args[2]), args[3], args[4] if len(args) > 4 else None, "--fp32" in sys.argv)
