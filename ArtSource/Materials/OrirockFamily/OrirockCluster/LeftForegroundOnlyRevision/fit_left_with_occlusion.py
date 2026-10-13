"""上部和右部固定，仅拟合左下标准方块的摆放，同时保留接地与真实截角。"""
from pathlib import Path
import json
import math
import numpy as np

OUT = Path(__file__).resolve().parent
state = json.loads((OUT / '当前左块与遮挡.json').read_text(encoding='utf-8'))
right, up, front = [np.array(state[k]) for k in ('right', 'up', 'front')]
projection = np.vstack([right, -up, front])
old = np.array(state['old_vertices'])
old_length = state['old_edge_length']
old_axes = np.column_stack([old[k] - old[0] for k in (1, 3, 4)]) / old_length
mesh_local = (np.array(state['mesh_vertices']) - old[0]) @ old_axes / old_length
bits = np.array([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0],
                 [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]], float)
fractions = np.array(state['cut']['fractions'])
source = np.array([[13, 113], [29, 143], [67, 142], [66, 81],
                   [49, 108], [58, 115], [59, 101]], float)
landmark_weights = np.array([1, 3, 1.5, 2, .7, .7, .7])[:, None]
checks = [c for c in state['checks'] if c['top_depth'] is not None]
sample_xy = np.array([c['pixel'] for c in checks])
sample_world = (sample_xy[:, :1] - 91) * .016 * right + (91 - sample_xy[:, 1:]) * .016 * up
top_depths = np.array([c['top_depth'] for c in checks])


def rotate(vector):
    angle = np.linalg.norm(vector)
    x, y, z = vector
    skew = np.array([[0, -z, y], [z, 0, -x], [-y, x, 0]])
    a = math.sin(angle) / angle if angle > 1e-8 else 1
    b = (1 - math.cos(angle)) / angle**2 if angle > 1e-8 else .5
    return np.eye(3) + skew * a + skew @ skew * b


def geometry(params):
    axes = rotate(params[:3]) @ old_axes
    side = np.exp(params[3])
    origin_xy = right * ((params[4] - 91) * .016) + up * ((91 - params[5]) * .016)
    local_world = mesh_local @ axes.T * side
    depth = (state['floor'] - origin_xy[2] - local_world[:, 2].min()) / front[2]
    origin = origin_xy + front * depth
    vertices = bits @ axes.T * side + origin
    screen = np.column_stack([91 + vertices @ right / .016, 91 - vertices @ up / .016])
    cap = np.array([screen[0] + (screen[j] - screen[0]) * t
                    for j, t in zip((1, 3, 4), fractions)])
    return axes, side, origin, vertices, screen, cap


def ray_front(params):
    axes, side, origin, vertices, screen, cap = geometry(params)
    origins = (sample_world + front * 5 - origin) @ axes / side
    direction = (-front) @ axes / side
    enter, leave = np.full(len(origins), -1e6), np.full(len(origins), 1e6)
    for axis in range(3):
        d = direction[axis]
        if abs(d) < 1e-8:
            enter = np.maximum(enter, np.where((origins[:, axis] >= 0) & (origins[:, axis] <= 1), -1e6, 1e6))
            continue
        t0, t1 = -origins[:, axis] / d, (1 - origins[:, axis]) / d
        enter = np.maximum(enter, np.minimum(t0, t1))
        leave = np.minimum(leave, np.maximum(t0, t1))
    # 被平面切掉的0号角：sum(local_axis / fraction) >= 1。
    start = origins @ (1 / fractions)
    slope = direction @ (1 / fractions)
    crossing = (1 - start) / slope
    if slope > 0:
        enter = np.maximum(enter, crossing)
    else:
        leave = np.minimum(leave, crossing)
    return 5 - enter, np.maximum(enter - leave, 0)


initial_xy = [91 + old[0] @ right / .016, 91 - old[0] @ up / .016]
initial = np.array([0, 0, 0, math.log(old_length), *initial_xy])
solutions = []
for occlusion_weight in (10, 30, 80, 150):
    params = initial.copy()

    def residual(p):
        axes, side, origin, vertices, screen, cap = geometry(p)
        landmarks = np.vstack([screen[[1, 2, 3, 4]], cap])
        foreground, outside = ray_front(p)
        behind = np.maximum(top_depths + .01 - foreground, 0)
        # 不通过翻面改变三个可见色面的归属。
        visible_axes = np.maximum(front @ axes + .03, 0)
        size = np.array([max(side / old_length - 1.35, 0), max(.80 - side / old_length, 0)]) * 50
        return np.r_[((landmarks - source) * landmark_weights).ravel(),
                      behind * occlusion_weight, outside * occlusion_weight,
                      visible_axes * 20, size]

    damping = 1.
    for iteration in range(120):
        error = residual(params)
        jac = np.column_stack([(residual(params + np.eye(6)[i] * 1e-5) - error) / 1e-5 for i in range(6)])
        delta = np.linalg.solve(jac.T @ jac + damping * np.eye(6), -jac.T @ error)
        candidate = params + delta
        if np.linalg.norm(residual(candidate)) < np.linalg.norm(error):
            params = candidate
            damping = max(damping * .5, 1e-7)
        else:
            damping *= 4
    axes, side, origin, vertices, screen, cap = geometry(params)
    depths, outside = ray_front(params)
    landmark_error = np.vstack([screen[[1, 2, 3, 4]], cap]) - source
    record = dict(occlusion_weight=occlusion_weight, axes_world=axes.tolist(),
                  edge_length=float(side), origin_world=origin.tolist(),
                  screen_vertices=screen.tolist(), cap_pixels=cap.tolist(),
                  landmark_rms_pixels=float(np.sqrt((landmark_error**2).mean())),
                  landmark_max_pixels=float(np.linalg.norm(landmark_error, axis=1).max()),
                  depth_gaps=(depths - top_depths).tolist(), outside_error=outside.tolist(),
                  min_world_z=float((mesh_local @ axes.T * side + origin)[:, 2].min()),
                  rotation_change_degrees=(params[:3] * 180 / math.pi).tolist())
    solutions.append(record)
    print(json.dumps({k: record[k] for k in ('occlusion_weight', 'edge_length',
          'landmark_rms_pixels', 'landmark_max_pixels', 'depth_gaps', 'outside_error')}, ensure_ascii=False))

(OUT / '左下接地与遮挡拟合.json').write_text(json.dumps(dict(
    note='仅左下标准方块的刚体候选；数值拟合不是视觉验收，必须查看实际渲染。',
    solutions=solutions), ensure_ascii=False, indent=2), encoding='utf-8')
