# Run in the helper tools' Python: the convex pieces of a mesh, by tools/colliders.py's own decomposition.
#   python run_colliders.py <tools dir> <in.npz> <tolerance> <out.npz>
import sys

import numpy as np

tools, src, tolerance, dst = sys.argv[1], sys.argv[2], float(sys.argv[3]), sys.argv[4]
sys.path.insert(0, tools)
import colliders

d = np.load(src)
v, f = colliders.weld(d["v"].astype(float), d["f"])
parts, mirrored = colliders.decompose(v, f, tolerance)
np.savez(dst, mirrored=mirrored, **{f"v{k}": pv for k, (pv, _) in enumerate(parts)}, **{f"f{k}": pf for k, (_, pf) in enumerate(parts)})
print(f"{len(parts)} convex pieces" + (" (one half, mirrored)" if mirrored else ""))
