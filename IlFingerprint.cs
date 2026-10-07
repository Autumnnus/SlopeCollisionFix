using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace SlopeCollisionFix
{
	// Produces a fingerprint of a method's original (metadata) IL that does not depend on metadata tokens.
	// Tokens are resolved to member names, so the same code yields the same fingerprint across builds.
	internal static class IlFingerprint
	{
		private static readonly Dictionary<short, OpCode> Ops = BuildOps();

		private static Dictionary<short, OpCode> BuildOps()
		{
			var ops = new Dictionary<short, OpCode>();
			foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
			{
				if (f.GetValue(null) is OpCode op)
					ops[op.Value] = op;
			}
			return ops;
		}

		public static string Compute(MethodInfo method)
		{
			byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
			Module module = method.Module;
			var sb = new StringBuilder();
			int i = 0;

			while (i < il.Length)
			{
				short value = il[i++];
				if (value == 0xFE)
					value = unchecked((short)(0xFE00 | il[i++]));

				OpCode op = Ops[value];
				sb.Append(op.Name);

				switch (op.OperandType)
				{
					case OperandType.InlineNone:
						break;
					case OperandType.ShortInlineBrTarget:
					case OperandType.ShortInlineI:
						sb.Append(' ').Append((sbyte)il[i]);
						i += 1;
						break;
					case OperandType.ShortInlineVar:
						sb.Append(' ').Append(il[i]);
						i += 1;
						break;
					case OperandType.InlineVar:
						sb.Append(' ').Append(BitConverter.ToUInt16(il, i));
						i += 2;
						break;
					case OperandType.InlineI:
					case OperandType.InlineBrTarget:
						sb.Append(' ').Append(BitConverter.ToInt32(il, i));
						i += 4;
						break;
					case OperandType.ShortInlineR:
						sb.Append(' ').Append(BitConverter.ToSingle(il, i).ToString("R", CultureInfo.InvariantCulture));
						i += 4;
						break;
					case OperandType.InlineR:
						sb.Append(' ').Append(BitConverter.ToDouble(il, i).ToString("R", CultureInfo.InvariantCulture));
						i += 8;
						break;
					case OperandType.InlineI8:
						sb.Append(' ').Append(BitConverter.ToInt64(il, i));
						i += 8;
						break;
					case OperandType.InlineSwitch:
						int count = BitConverter.ToInt32(il, i);
						i += 4;
						sb.Append(' ').Append(count);
						for (int k = 0; k < count; k++, i += 4)
							sb.Append(' ').Append(BitConverter.ToInt32(il, i));
						break;
					case OperandType.InlineString:
						sb.Append(' ').Append(module.ResolveString(BitConverter.ToInt32(il, i)));
						i += 4;
						break;
					case OperandType.InlineSig:
						sb.Append(" sig");
						i += 4;
						break;
					default:
						MemberInfo member = module.ResolveMember(BitConverter.ToInt32(il, i))!;
						sb.Append(' ').Append(member.DeclaringType?.FullName).Append("::").Append(member);
						i += 4;
						break;
				}

				sb.Append('\n');
			}

			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
		}
	}
}
