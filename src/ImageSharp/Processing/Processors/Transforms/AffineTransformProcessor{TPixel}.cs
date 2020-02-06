// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Processing.Processors.Transforms
{
    /// <summary>
    /// Provides the base methods to perform affine transforms on an image.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal class AffineTransformProcessor<TPixel> : TransformProcessor<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        private Size targetSize;
        private Matrix3x2 transformMatrix;
        private readonly IResampler resampler;

        /// <summary>
        /// Initializes a new instance of the <see cref="AffineTransformProcessor{TPixel}"/> class.
        /// </summary>
        /// <param name="configuration">The configuration which allows altering default behaviour or extending the library.</param>
        /// <param name="definition">The <see cref="AffineTransformProcessor"/> defining the processor parameters.</param>
        /// <param name="source">The source <see cref="Image{TPixel}"/> for the current processor instance.</param>
        /// <param name="sourceRectangle">The source area to process for the current processor instance.</param>
        public AffineTransformProcessor(Configuration configuration, AffineTransformProcessor definition, Image<TPixel> source, Rectangle sourceRectangle)
            : base(configuration, source, sourceRectangle)
        {
            this.targetSize = definition.TargetDimensions;
            this.transformMatrix = definition.TransformMatrix;
            this.resampler = definition.Sampler;
        }

        protected override Size GetTargetSize() => this.targetSize;

        /// <inheritdoc/>
        protected override void OnFrameApply(ImageFrame<TPixel> source, ImageFrame<TPixel> destination)
        {
            // Handle transforms that result in output identical to the original.
            if (this.transformMatrix.Equals(default) || this.transformMatrix.Equals(Matrix3x2.Identity))
            {
                // The clone will be blank here copy all the pixel data over
                source.GetPixelSpan().CopyTo(destination.GetPixelSpan());
                return;
            }

            int width = this.targetSize.Width;
            var targetBounds = new Rectangle(Point.Empty, this.targetSize);
            Configuration configuration = this.Configuration;

            // Convert from screen to world space.
            Matrix3x2.Invert(this.transformMatrix, out Matrix3x2 matrix);

            if (this.resampler is NearestNeighborResampler)
            {
                Rectangle sourceBounds = this.SourceRectangle;
                var nearestRowAction = new NearestNeighborRowIntervalAction(ref sourceBounds, ref matrix, width, source, destination);

                ParallelRowIterator.IterateRows(
                    targetBounds,
                    configuration,
                    in nearestRowAction);

                return;
            }

            using var kernelMap = new TransformKernelMap(configuration, source.Size(), destination.Size(), this.resampler);
            var rowAction = new RowIntervalAction(configuration, kernelMap, ref matrix, width, source, destination);

            ParallelRowIterator.IterateRows<RowIntervalAction, Vector4>(
                targetBounds,
                configuration,
                in rowAction);
        }

        private readonly struct NearestNeighborRowIntervalAction : IRowIntervalAction
        {
            private readonly Rectangle bounds;
            private readonly Matrix3x2 matrix;
            private readonly int maxX;
            private readonly ImageFrame<TPixel> source;
            private readonly ImageFrame<TPixel> destination;

            [MethodImpl(InliningOptions.ShortMethod)]
            public NearestNeighborRowIntervalAction(
                ref Rectangle bounds,
                ref Matrix3x2 matrix,
                int maxX,
                ImageFrame<TPixel> source,
                ImageFrame<TPixel> destination)
            {
                this.bounds = bounds;
                this.matrix = matrix;
                this.maxX = maxX;
                this.source = source;
                this.destination = destination;
            }

            [MethodImpl(InliningOptions.ShortMethod)]
            public void Invoke(in RowInterval rows)
            {
                for (int y = rows.Min; y < rows.Max; y++)
                {
                    Span<TPixel> destRow = this.destination.GetPixelRowSpan(y);

                    for (int x = 0; x < this.maxX; x++)
                    {
                        var point = Point.Transform(new Point(x, y), this.matrix);
                        if (this.bounds.Contains(point.X, point.Y))
                        {
                            destRow[x] = this.source[point.X, point.Y];
                        }
                    }
                }
            }
        }

        private readonly struct RowIntervalAction : IRowIntervalAction<Vector4>
        {
            private readonly Configuration configuration;
            private readonly TransformKernelMap kernelMap;
            private readonly Matrix3x2 matrix;
            private readonly int maxX;
            private readonly ImageFrame<TPixel> source;
            private readonly ImageFrame<TPixel> destination;

            [MethodImpl(InliningOptions.ShortMethod)]
            public RowIntervalAction(
                Configuration configuration,
                TransformKernelMap kernelMap,
                ref Matrix3x2 matrix,
                int maxX,
                ImageFrame<TPixel> source,
                ImageFrame<TPixel> destination)
            {
                this.configuration = configuration;
                this.kernelMap = kernelMap;
                this.matrix = matrix;
                this.maxX = maxX;
                this.source = source;
                this.destination = destination;
            }

            [MethodImpl(InliningOptions.ShortMethod)]
            public void Invoke(in RowInterval rows, Memory<Vector4> memory)
            {
                Span<Vector4> vectorSpan = memory.Span;
                for (int y = rows.Min; y < rows.Max; y++)
                {
                    Span<TPixel> targetRowSpan = this.destination.GetPixelRowSpan(y);
                    PixelOperations<TPixel>.Instance.ToVector4(this.configuration, targetRowSpan, vectorSpan);
                    ref float ySpanRef = ref this.kernelMap.GetYStartReference(y);
                    ref float xSpanRef = ref this.kernelMap.GetXStartReference(y);

                    for (int x = 0; x < this.maxX; x++)
                    {
                        // Use the single precision position to calculate correct bounding pixels
                        // otherwise we get rogue pixels outside of the bounds.
                        var point = Vector2.Transform(new Vector2(x, y), this.matrix);
                        this.kernelMap.Convolve(
                            point,
                            x,
                            ref ySpanRef,
                            ref xSpanRef,
                            this.source.PixelBuffer,
                            vectorSpan);
                    }

                    PixelOperations<TPixel>.Instance.FromVector4Destructive(
                        this.configuration,
                        vectorSpan,
                        targetRowSpan);
                }
            }
        }
    }
}
