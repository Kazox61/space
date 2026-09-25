using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Space.Client.LevelAuthoring;

/// <summary>Shared by entity and collider recipes: transform conversion and recipe/override layering.</summary>
public static class LevelMarkers {
	/// <summary>Converts a marker's global transform to fixed point. Markers must have unit, non-mirrored scale.</summary>
	public static Fixed.FWorldTransform ToFixed(Transform3D globalTransform, string sourcePath) {
		var scale = globalTransform.Basis.Scale;
		if (!scale.IsEqualApprox(Vector3.One) || globalTransform.Basis.Determinant() <= 0f) {
			throw new InvalidDataException($"{sourcePath}: level markers require unit, non-mirrored scale; set sizes in components instead.");
		}

		var position = globalTransform.Origin;
		var rotation = globalTransform.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
		if (!position.IsFinite() || !rotation.IsFinite()) {
			throw new InvalidDataException($"{sourcePath}: transform must be finite.");
		}

		return new Fixed.FWorldTransform(
			new Fixed.FPos(
				Fixed64.FConversions.ToFP(position.X),
				Fixed64.FConversions.ToFP(position.Y),
				Fixed64.FConversions.ToFP(position.Z)
			),
			new Fixed32.FQuaternion(
				Fixed32.FConversions.ToFP(rotation.X),
				Fixed32.FConversions.ToFP(rotation.Y),
				Fixed32.FConversions.ToFP(rotation.Z),
				Fixed32.FConversions.ToFP(rotation.W)
			)
		);
	}

	/// <summary>
	/// Applies one layer (a recipe's components, then a marker's overrides). A later layer overwrites what an
	/// earlier one set; within one layer each component kind may appear once.
	/// </summary>
	public static void ApplyLayer<TComponent, TKind>(
		IEnumerable<TComponent> components,
		Func<TComponent, TKind> kind,
		Action<TComponent> apply,
		string sourcePath,
		string layerName
	) where TComponent : class {
		var present = new HashSet<TKind>();
		foreach (var component in components) {
			if (component is null) {
				throw new InvalidDataException($"{sourcePath}: {layerName} contains an empty component.");
			}
			var componentKind = kind(component);
			if (!present.Add(componentKind)) {
				throw new InvalidDataException($"{sourcePath}: {layerName} contains duplicate {componentKind} components.");
			}
			try {
				apply(component);
			} catch (Exception exception) {
				throw new InvalidDataException($"{sourcePath}: invalid {componentKind} component.", exception);
			}
		}
	}
}
