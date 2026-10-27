"""按可辨认的原图棱线拟合标准正方体；只允许旋转、等比缩放和平移。"""
from pathlib import Path
import json
import math
import numpy as np

OUT = Path(__file__).resolve().parent
rows = json.loads((OUT.parent / 'SurfaceMatchRevision' / '当前块体坐标.json').read_text(encoding='utf-8'))
right = np.array([.8526012301, .5225620866, 7.45058e-8])
up = np.array([-.2518555522, .4109220207, .8761917353])
front = np.array([.4578645229, -.7470421791, .4819627404])
projection = np.vstack([right, -up, front])
bits = np.array([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0],
                 [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]], float)


def rotation(vector):
    angle = np.linalg.norm(vector)
    x, y, z = vector
    skew = np.array([[0, -z, y], [z, 0, -x], [-y, x, 0]])
    a = math.sin(angle) / angle if angle > 1e-8 else 1
    b = (1 - math.cos(angle)) / angle**2 if angle > 1e-8 else .5
    return np.eye(3) + skew * a + skew @ skew * b


specs = [
    dict(block=0, corner=1, neighbors=[0, 2, 5], fractions=[.35, .52, .25],
         vertex_ids=[0, 2, 3, 4, 5],
         vertex_targets=[[109, 72], [46, 50], [68, 95], [143, 82], [126, 36]],
         cap_targets=[[98, 42], [72, 25], [102, 24]],
         weights=[1.8, 1, 1, 1, 1, 2, 2, 2]),
    dict(block=1, corner=0, neighbors=[1, 3, 4], fractions=[.15, .12, .20],
         vertex_ids=[0, 1, 2, 3, 4],
         vertex_targets=[[56, 110], [13, 113], [29, 143], [67, 142], [66, 81]],
         cap_targets=[[49, 108], [58, 115], [59, 101]],
         weights=[1, 1, 1.8, 1.8, 1.5, 1, 1, 1]),
    dict(block=4, corner=0, neighbors=[1, 3, 4], fractions=[.13, .20, .10],
         vertex_ids=[0, 1, 3, 4],
         vertex_targets=[[156, 98], [126, 88], [155, 137], [168, 65]],
         cap_targets=[[146, 94], [155, 107], [158, 91]],
         # 左端角点被上块遮挡，只作弱约束，不把遮挡交点冒充立方体角点。
         weights=[1.3, .25, 1.6, 1.6, 1.5, 1.5, 1.5]),
]

results = []
for spec in specs:
    row = rows[spec['block']]
    old = np.array(row['world_vertices'])
    old_rotation = np.column_stack([old[k] - old[0] for k in (1, 3, 4)]) / row['edge_length']
    target = np.array(spec['vertex_targets'] + spec['cap_targets'], float)
    weights = np.array(spec['weights'])[:, None]
    params = np.array([0, 0, 0, math.log(row['edge_length'] / .016),
                       *row['verts'][0][:2], *spec['fractions']])

    def compute(p):
        basis = rotation(p[:3]) @ old_rotation
        size = np.exp(p[3])
        vertices = bits @ (projection[:2] @ basis).T * size + p[4:6]
        corner = vertices[spec['corner']]
        cap = np.array([corner + (vertices[j] - corner) * t
                        for j, t in zip(spec['neighbors'], p[6:9])])
        predicted = np.vstack([vertices[spec['vertex_ids']], cap])
        return vertices, cap, predicted, basis, size

    def residual(p):
        return ((compute(p)[2] - target) * weights).ravel()

    initial_rms = float(np.sqrt(((compute(params)[2] - target)**2).mean()))
    damping = 1.
    for iteration in range(120):
        error = residual(params)
        jacobian = np.column_stack([(residual(params + np.eye(9)[j] * 1e-5) - error) / 1e-5
                                    for j in range(9)])
        delta = np.linalg.solve(jacobian.T @ jacobian + damping * np.eye(9),
                                -jacobian.T @ error)
        candidate = params + delta
        candidate[6:9] = np.clip(candidate[6:9], .03, .93)
        if np.linalg.norm(residual(candidate)) < np.linalg.norm(error):
            params = candidate
            damping = max(damping * .5, 1e-7)
        else:
            damping *= 4
    vertices, cap, predicted, basis, size = compute(params)
    item = dict(spec, world_rotation=basis.tolist(), edge_length=float(size * .016),
                origin_pixels=params[4:6].tolist(), fractions=params[6:9].tolist(),
                screen_vertices=vertices.tolist(), cap_pixels=cap.tolist(),
                initial_landmark_rms_pixels=initial_rms,
                landmark_rms_pixels=float(np.sqrt(((predicted - target)**2).mean())),
                landmark_max_error_pixels=float(np.max(np.linalg.norm(predicted - target, axis=1))))
    results.append(item)
    print(json.dumps({k: item[k] for k in ('block', 'edge_length', 'fractions',
          'initial_landmark_rms_pixels', 'landmark_rms_pixels', 'landmark_max_error_pixels')}, ensure_ascii=False))

(OUT / '标准方块正面拟合.json').write_text(json.dumps(dict(
    note='手绘棱线的近似拟合，不表示视觉验收通过；截角外只作等比刚体变换。',
    blocks=results), ensure_ascii=False, indent=2), encoding='utf-8')
