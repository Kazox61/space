"""Validate the GLB in a disposable background Blender process.

blender --background blender/models/huntress_locomotion.blend \
    --python blender/validate_huntress_locomotion.py

Checks clip membership/durations, loop seams, grounded run contacts, stationary
root, and nearest-vertex deformation agreement after reimport at every frame.
Does not save the temporary imported objects into the authoring file.
"""

import json
import math
from pathlib import Path
import struct

import bpy
from mathutils import Matrix
from mathutils.kdtree import KDTree


def validate():
    root = Path(bpy.data.filepath).resolve().parents[2]
    path = root / 'Client/assets/player/model/huntress_locomotion.glb'
    with path.open('rb') as stream:
        magic, version, length = struct.unpack('<4sII', stream.read(12))
        assert magic == b'glTF' and version == 2
        size, kind = struct.unpack('<I4s', stream.read(8))
        assert kind == b'JSON'
        gltf = json.loads(stream.read(size))
    durations = {'idle': 33 / 30, 'run': 16 / 30,
                  'jump': 11 / 30, 'fall': 16 / 30, 'land': 8 / 30,
                  'tilt_l': 8 / 30, 'tilt_r': 8 / 30,
                  'bow_hold': 1 / 30, 'attack': 32 / 30}
    assert {a['name'] for a in gltf['animations']} == set(durations)
    assert len(gltf['meshes']) == len(gltf['skins']) == 1
    assert len(gltf['images']) == len(gltf['materials']) == 1
    assert 'cameras' not in gltf
    for animation in gltf['animations']:
        times = [gltf['accessors'][s['input']] for s in animation['samplers']]
        assert abs(min(t['min'][0] for t in times)) < 1e-6
        assert abs(max(t['max'][0] for t in times) - durations[animation['name']]) < 1e-6

    source = bpy.data.objects['hunter_locomotion']
    source_mesh = bpy.data.objects['HunterLocomotionModel']
    weapon_mask = source_mesh.modifiers['Hide weapon for neutral locomotion']
    weapon_mask.show_viewport = False
    weapon_group = source_mesh.vertex_groups[weapon_mask.vertex_group].index
    character_indices = [
        vertex.index for vertex in source_mesh.data.vertices
        if not any(group.group == weapon_group and group.weight > 0
                   for group in vertex.groups)
    ]
    before_objects = set(bpy.data.objects)
    before_actions = set(bpy.data.actions)
    bpy.ops.import_scene.gltf(filepath=str(path))
    imported = set(bpy.data.objects) - before_objects
    rig = next(o for o in imported if o.type == 'ARMATURE')
    mesh = next(o for o in imported if o.type == 'MESH'
                and any(m.type == 'ARMATURE' for m in o.modifiers))
    actions = set(bpy.data.actions) - before_actions
    imported_actions = {name: next(a for a in actions if a.name.startswith(name))
                        for name in durations}
    source.animation_data.use_nla = False
    rig.animation_data.use_nla = False
    scene = bpy.context.scene
    depsgraph = bpy.context.evaluated_depsgraph_get()
    report = {}

    def vertices(obj):
        evaluated = obj.evaluated_get(depsgraph)
        data = evaluated.to_mesh()
        try:
            return [evaluated.matrix_world @ v.co for v in data.vertices]
        finally:
            evaluated.to_mesh_clear()

    for name in durations:
        action = bpy.data.actions[name]
        for pose_bone in source.pose.bones:
            pose_bone.matrix_basis = Matrix.Identity(4)
        for pose_bone in rig.pose.bones:
            pose_bone.matrix_basis = Matrix.Identity(4)
        source.animation_data.action = action
        source.animation_data.action_slot = action.slots[0]
        rig.animation_data.action = imported_actions[name]
        rig.animation_data.action_slot = imported_actions[name].slots[0]
        # Source keys start at 1; the GLB is deliberately slid to time zero.
        # Offset imported F-curves temporarily to compare identical poses.
        for layer in imported_actions[name].layers:
            for strip in layer.strips:
                for bag in strip.channelbags:
                    for curve in bag.fcurves:
                        for key in curve.keyframe_points:
                            key.co.x += 1
                            key.handle_left.x += 1
                            key.handle_right.x += 1
                        curve.update()
        worst = 0
        first = None
        hip_heights = []
        contact_error = 0
        attack_release_yaw = None
        for frame in range(1, int(action.frame_range[1]) + 1):
            scene.frame_set(frame)
            bpy.context.view_layer.update()
            points = vertices(source_mesh)
            character_points = [points[index] for index in character_indices]
            tree = KDTree(len(points))
            for i, point in enumerate(points):
                tree.insert(point, i)
            tree.balance()
            imported_points = vertices(mesh)
            worst = max(worst, max(tree.find(p)[2] for p in imported_points))
            imported_tree = KDTree(len(imported_points))
            for i, point in enumerate(imported_points):
                imported_tree.insert(point, i)
            imported_tree.balance()
            worst = max(worst, max(imported_tree.find(p)[2] for p in points))
            matrices = {p.name: p.matrix.copy() for p in source.pose.bones if p.bone.use_deform}
            if first is None:
                first = matrices
            assert source.pose.bones['Root'].location.length < 1e-6
            hip_heights.append(source.pose.bones['Hip'].matrix.translation.z)
            if name == 'attack' and frame == 15:
                crossbow = source.pose.bones['Crossbow']
                direction = crossbow.tail - crossbow.head
                attack_release_yaw = math.degrees(math.atan2(direction.x, -direction.y))
                assert abs(attack_release_yaw) < 1, ('attack release yaw', attack_release_yaw)
            if name == 'run':
                assert min(p.z for p in character_points) >= -1e-4, ('ground penetration', frame)
                if frame in (1, 9):
                    assert min(p.z for p in character_points) > 0.025, ('missing flight', frame)
                for side, contact in [('L', 3), ('R', 11)]:
                    if frame in (contact, contact + 2):
                        foot = source.pose.bones['Foot1DB.' + side].matrix.translation
                        contact_error = max(contact_error, abs(foot.z))
        seam = max(abs(first[n][i][j] - matrices[n][i][j])
                   for n in first for i in range(4) for j in range(4))
        assert worst < 1e-4, (name, 'deformation mismatch', worst)
        if name in ('idle', 'run'):
            assert seam < 1e-5, (name, 'loop mismatch', seam)
        if name == 'run':
            assert action.get('source_action') == 'review_run_v3'
            assert contact_error < 0.001, contact_error
        if name == 'fall':
            assert max(hip_heights) - min(hip_heights) < 1e-6
        report[name] = {'seconds': durations[name], 'max_vertex_error': worst,
                        'loop_matrix_error': seam if action.use_cyclic else None,
                        'hip_z_range': [min(hip_heights), max(hip_heights)],
                        'release_yaw': attack_release_yaw}
    return {'clips': report, 'joints': len(gltf['skins'][0]['joints']),
            'exported_vertices': sum(gltf['accessors'][p['attributes']['POSITION']]['count']
                                     for p in gltf['meshes'][0]['primitives'])}


VALIDATION_RESULT = validate()
print(json.dumps(VALIDATION_RESULT, indent=2))
