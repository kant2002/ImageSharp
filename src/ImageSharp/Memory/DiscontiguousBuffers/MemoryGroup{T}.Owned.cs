// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.Memory.Internals;

namespace SixLabors.ImageSharp.Memory
{
    internal abstract partial class MemoryGroup<T>
    {
        /// <summary>
        /// A <see cref="MemoryGroup{T}"/> implementation that owns the underlying memory buffers.
        /// </summary>
        public sealed class Owned : MemoryGroup<T>, IEnumerable<Memory<T>>
        {
            private IMemoryOwner<T>[] memoryOwners;

            // When user calls DefaultMemoryAllocator.ReleaseRetainedResources(), we want
            // UniformByteArrayPool to be released and GC-d ASAP. We need to prevent existing Image-s and buffers
            // to keep it alive, so we use WeakReference instead of directly referencing the pool.
            private WeakReference<UniformByteArrayPool> poolReference;
            private byte[][] pooledArrays;

            public Owned(IMemoryOwner<T>[] memoryOwners, int bufferLength, long totalLength, bool swappable)
                : base(bufferLength, totalLength)
            {
                this.memoryOwners = memoryOwners;
                this.Swappable = swappable;
                this.View = new MemoryGroupView<T>(this);
            }

            public Owned(UniformByteArrayPool pool, byte[][] pooledArrays, int bufferLength, long totalLength, int sizeOfLastBuffer)
                : this(CreateBuffers(pool, pooledArrays, bufferLength, sizeOfLastBuffer), bufferLength, totalLength, true)
            {
                this.pooledArrays = pooledArrays;
                this.poolReference = new WeakReference<UniformByteArrayPool>(pool);

                // Track all WeakReference's to make sure they are not finalized before ~FinalizableBuffer<T>()
                WeakReferenceTracker.Add(this.poolReference);
            }

            ~Owned()
            {
                this.Dispose(false);
            }

            public bool Swappable { get; }

            private bool IsDisposed => this.memoryOwners == null;

            public override int Count
            {
                [MethodImpl(InliningOptions.ShortMethod)]
                get
                {
                    this.EnsureNotDisposed();
                    return this.memoryOwners.Length;
                }
            }

            public override Memory<T> this[int index]
            {
                get
                {
                    this.EnsureNotDisposed();
                    return this.memoryOwners[index].Memory;
                }
            }

            private static IMemoryOwner<T>[] CreateBuffers(UniformByteArrayPool pool, byte[][] pooledArrays, int bufferLength, int sizeOfLastBuffer)
            {
                var result = new IMemoryOwner<T>[pooledArrays.Length];
                for (int i = 0; i < pooledArrays.Length - 1; i++)
                {
                    result[i] = new UniformByteArrayPool.Buffer<T>(pooledArrays[i], bufferLength, pool);
                }

                result[result.Length - 1] = new UniformByteArrayPool.Buffer<T>(pooledArrays[pooledArrays.Length - 1], sizeOfLastBuffer, pool);
                return result;
            }

            /// <inheritdoc/>
            [MethodImpl(InliningOptions.ShortMethod)]
            public override MemoryGroupEnumerator<T> GetEnumerator()
            {
                return new MemoryGroupEnumerator<T>(this);
            }

            /// <inheritdoc/>
            IEnumerator<Memory<T>> IEnumerable<Memory<T>>.GetEnumerator()
            {
                this.EnsureNotDisposed();
                return this.memoryOwners.Select(mo => mo.Memory).GetEnumerator();
            }

            protected override void Dispose(bool disposing)
            {
                if (this.IsDisposed)
                {
                    return;
                }

                this.View.Invalidate();

                if (this.poolReference != null && this.poolReference.TryGetTarget(out UniformByteArrayPool pool))
                {
                    // Dispose(false) could be called from a finalizer, so we can return the rented arrays,
                    // even if user code is leaking.
                    // We are fine to do this, since byte[][] and UniformByteArrayPool are not finalizable.
                    WeakReferenceTracker.Remove(this.poolReference);
                    pool.Return(this.pooledArrays);
                    foreach (IMemoryOwner<T> memoryOwner in this.memoryOwners)
                    {
                        ((UniformByteArrayPool.Buffer<T>)memoryOwner).MarkDisposed();
                    }
                }
                else if (disposing)
                {
                    foreach (IMemoryOwner<T> memoryOwner in this.memoryOwners)
                    {
                        memoryOwner.Dispose();
                    }
                }

                this.memoryOwners = null;
                this.IsValid = false;
                this.poolReference = null;
                this.pooledArrays = null;
            }

            [MethodImpl(InliningOptions.ShortMethod)]
            private void EnsureNotDisposed()
            {
                if (this.memoryOwners is null)
                {
                    ThrowObjectDisposedException();
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void ThrowObjectDisposedException()
            {
                throw new ObjectDisposedException(nameof(MemoryGroup<T>));
            }

            internal static void SwapContents(Owned a, Owned b)
            {
                a.EnsureNotDisposed();
                b.EnsureNotDisposed();

                IMemoryOwner<T>[] tempOwners = a.memoryOwners;
                long tempTotalLength = a.TotalLength;
                int tempBufferLength = a.BufferLength;
                WeakReference<UniformByteArrayPool> tempPoolReference = a.poolReference;
                byte[][] tempPooledArrays = a.pooledArrays;

                a.memoryOwners = b.memoryOwners;
                a.TotalLength = b.TotalLength;
                a.BufferLength = b.BufferLength;
                a.poolReference = b.poolReference;
                a.pooledArrays = b.pooledArrays;

                b.memoryOwners = tempOwners;
                b.TotalLength = tempTotalLength;
                b.BufferLength = tempBufferLength;
                b.poolReference = tempPoolReference;
                b.pooledArrays = tempPooledArrays;

                a.View.Invalidate();
                b.View.Invalidate();
                a.View = new MemoryGroupView<T>(a);
                b.View = new MemoryGroupView<T>(b);
            }
        }
    }
}
