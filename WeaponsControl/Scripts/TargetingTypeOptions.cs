﻿using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.ModAPI;
using VRageMath;

namespace Rynchodon.Autopilot.Weapons
{
	/// <summary>
	/// Defined in the order of precedence
	/// </summary>
	[Flags]
	public enum TargetType : ushort
	{
		None = 0,
		Missile = 1 << 0,
		Meteor = 1 << 1,
		Character = 1 << 2,
		/// <summary>Will track floating object and large and small grids</summary>
		Moving = 1 << 3,
		LargeGrid = 1 << 4,
		SmallGrid = 1 << 5,
		Station = 1 << 6,
		/// <summary>Destroy every terminal block on grids</summary>
		Destroy = 1 << 8
	}

	public class TargetingOptions
	{
		public TargetType CanTarget = TargetType.None;
		public bool CanTargetType(TargetType type)
		{ return (CanTarget & type) != 0; }

		public List<string> blocksToTarget = new List<string>();
	}

	public class Target
	{
		public readonly IMyEntity Entity;
		public readonly TargetType TType;
		public Vector3? FiringDirection;
		public Vector3? InterceptionPoint;

		/// <summary>
		/// Creates a target of type None with Entity as null.
		/// </summary>
		public Target()
		{
			this.Entity = null;
			this.TType = TargetType.None;
		}

		public Target(IMyEntity entity, TargetType tType)
		{
			this.Entity = entity;
			this.TType = tType;
		}
	}

	public class Ammo
	{
		public readonly MyAmmoDefinition Definition;
		public readonly float TimeToMaxSpeed;
		public readonly float DistanceToMaxSpeed;

		public Ammo(MyAmmoDefinition Definiton)
		{
			this.Definition = Definiton;

			MyMissileAmmoDefinition asMissile = Definition as MyMissileAmmoDefinition;
			if (asMissile != null && !asMissile.MissileSkipAcceleration)
			{
				this.TimeToMaxSpeed = (asMissile.DesiredSpeed - asMissile.MissileInitialSpeed) / asMissile.MissileAcceleration;
				this.DistanceToMaxSpeed = (asMissile.DesiredSpeed + asMissile.MissileInitialSpeed) / 2 * TimeToMaxSpeed;
			}
			else
			{
				this.TimeToMaxSpeed = 0;
				this.DistanceToMaxSpeed = 0;
			}
		}
	}
}