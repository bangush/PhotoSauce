﻿using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

using PhotoSauce.MagicScaler.Interpolators;

namespace PhotoSauce.MagicScaler
{
	internal class KernelMap<T> : IDisposable where T : unmanaged
	{
		private readonly int mapLen;
		private readonly IMemoryOwner<byte> map;

		public int InPixels { get; }
		public int OutPixels { get; }
		public int Samples { get; }
		public int Channels { get; }
		public ReadOnlySpan<byte> Map => map.Memory.Span.Slice(0, mapLen);

		private static Exception getTypeException() => new NotSupportedException(nameof(T) + " must be int or float");

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		unsafe private static T convertWeight(double d)
		{
			if (typeof(T) == typeof(int))
				return (T)(object)MathUtil.Fix15(d);
			if (typeof(T) == typeof(float))
				return (T)(object)(float)d;

			throw getTypeException();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		unsafe private static T add(T a, T b)
		{
			if (typeof(T) == typeof(int))
				return (T)(object)((int)(object)a + (int)(object)b);
			if (typeof(T) == typeof(float))
				return (T)(object)((float)(object)a + (float)(object)b);

			throw getTypeException();
		}

		unsafe private static void fillKernelWeights(IInterpolator interpolator, double* kernel, int ksize, double start, double center, double scale)
		{
			double sum = 0d;
			for (int i = 0; i < ksize; i++)
			{
				double weight = interpolator.GetValue(Math.Abs((start - center + i) * scale));
				sum += weight;
				kernel[i] = weight;
			}

			sum = 1d / sum;
			for (int i = 0; i < ksize; i++)
				kernel[i] *= sum;
		}

		private static int getKernelPadding(int isize, int ksize, int channels)
		{
			int kpad = 0, inc = channels == 2 || channels == 3 ? 4 : Vector<T>.Count;
			if (ksize * channels % (inc * channels) > 1)
				kpad = MathUtil.DivCeiling(ksize * channels, inc * channels) * inc - ksize;

			return ksize + kpad > isize ? 0 : kpad;
		}

		private KernelMap(int inPixels, int outPixels, int samples, int channels)
		{
			InPixels = inPixels;
			OutPixels = outPixels;
			Samples = samples;
			Channels = channels;

			mapLen = OutPixels * (Samples * Channels * Unsafe.SizeOf<T>() + sizeof(int));
			map = MemoryPool<byte>.Shared.Rent(mapLen);
			map.Memory.Span.Slice(0, mapLen).Clear();
		}

		unsafe private KernelMap<T> clamp()
		{
			fixed (byte* mstart = Map)
			{
				int samp = Samples, ipix = InPixels, chan = Channels;

				int* mp = (int*)mstart;
				int* mpe = (int*)(mstart + mapLen);
				while (mp < mpe)
				{
					int start = *mp;
					if (start < 0)
					{
						int o = 0 - start;

						*mp = 0;
						int* mpw = mp + 1;
						for (int k = 0; k < chan; k++)
						{
							var a = default(T);
							for (int j = 0; j <= o; j++)
								a = add(a, Unsafe.Read<T>(mpw + j * chan + k));

							Unsafe.Write(mpw + k, a);

							for (int j = 1; j < samp; j++)
								Unsafe.Write(mpw + j * chan + k, j < samp - o ? Unsafe.Read<T>(mpw + j * chan + o * chan + k) : default);
						}
					}
					else if (start + samp > ipix)
					{
						int ns = ipix - samp, last = samp - 1, o = start - ns;

						*mp = ns;
						int* mpw = mp + 1;
						for (int k = 0; k < chan; k++)
						{
							var a = default(T);
							for (int j = 0; j <= o; j++)
								a = add(a, Unsafe.Read<T>(mpw + last * chan - j * chan + k));

							Unsafe.Write(mpw + last * chan + k, a);

							for (int j = last - 1; j >= 0; j--)
								Unsafe.Write(mpw + j * chan + k, j >= o ? Unsafe.Read<T>(mpw + j * chan - o * chan + k) : default);
						}
					}

					mp += (samp * chan + 1);
				}
			}

			return this;
		}

		unsafe public static KernelMap<T> MakeScaleMap(int isize, int osize, InterpolationSettings interpolator, int ichannels, bool vectored)
		{
			double offs = interpolator.WeightingFunction.Support < 0.1 ? 0.5 : 0.0;
			double ratio = Math.Min((double)osize / isize, 1d);
			double cscale = ratio / interpolator.Blur;
			double support = Math.Min(interpolator.WeightingFunction.Support / cscale, isize / 2d);

			int channels = vectored ? ichannels : 1;
			int ksize = (int)Math.Ceiling(support * 2d);
			int kpad = vectored ? getKernelPadding(isize, ksize, channels) : 0;

			var map = new KernelMap<T>(isize, osize, ksize + kpad, channels);
			fixed (byte* mstart = map.Map)
			{
				int* mp = (int*)mstart;
				double* kp = stackalloc double[ksize];

				double inc = (double)isize / osize;
				double spoint = ((double)isize - osize) / (osize * 2d) + offs;
				for (int i = 0; i < osize; i++)
				{
					int start = (int)(spoint + support) - ksize + 1;
					fillKernelWeights(interpolator.WeightingFunction, kp, ksize, start, spoint, cscale);

					spoint += inc;
					*mp++ = start;

					for (int j = 0; j < ksize; j++)
					{
						var w = convertWeight(kp[j]);
						for (int k = 0; k < channels; k++)
							Unsafe.Write(mp++, w);
					}

					mp += kpad * channels;
				}
			}

			return map.clamp();
		}

		unsafe public static KernelMap<T> MakeBlurMap(int size, double radius, int ichannels, bool vectored)
		{
			var interpolator = new GaussianInterpolator(radius);

			int channels = vectored ? ichannels : 1;
			int dist = (int)Math.Ceiling(interpolator.Support);
			int ksize = Math.Min(dist * 2 + 1, size);
			int kpad = vectored ? getKernelPadding(size, ksize, channels) : 0;

			var map = new KernelMap<T>(size, size, ksize + kpad, channels);
			fixed (byte* mstart = map.Map)
			{
				int* mp = (int*)mstart;
				double* kp = stackalloc double[ksize];
				fillKernelWeights(interpolator, kp, ksize, 0d, dist, 1d);

				for (int i = 0; i < size; i++)
				{
					int start = i - ksize / 2;
					*mp++ = start;

					for (int j = 0; j < ksize; j++)
					{
						var w = convertWeight(kp[j]);
						for (int k = 0; k < channels; k++)
							Unsafe.Write(mp++, w);
					}

					mp += kpad * channels;
				}
			}

			return map.clamp();
		}

		public void Dispose()
		{
			map.Dispose();
		}
	}
}