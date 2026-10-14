use crate::{
    App, Bounds, Element, GlobalElementId, Hitbox, InspectorElementId, InteractiveElement,
    Interactivity, IntoElement, LayoutId, Pixels, Point, Radians, SharedString, Size,
    StyleRefinement, Styled, TransformationMatrix, Window, geometry::Negate as _, point, px,
    radians, size,
};
use util::ResultExt;

/// How an SVG's sampled alpha is composited on Windows.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum SvgAlphaMode {
    /// Apply the existing text contrast and gamma correction.
    #[default]
    ContrastCorrected = 0,
    /// Preserve linear coverage when translating antialiased artwork.
    Coverage = 1,
}

/// An SVG element.
pub struct Svg {
    interactivity: Interactivity,
    transformation: Option<Transformation>,
    path: Option<SharedString>,
    alpha_mode: SvgAlphaMode,
}

/// Create a new SVG element.
#[track_caller]
pub fn svg() -> Svg {
    Svg {
        interactivity: Interactivity::new(),
        transformation: None,
        path: None,
        alpha_mode: SvgAlphaMode::default(),
    }
}

impl Svg {
    /// Set the path to the SVG file for this element.
    pub fn path(mut self, path: impl Into<SharedString>) -> Self {
        self.path = Some(path.into());
        self
    }

    /// Transform the SVG element with the given transformation.
    /// Note that this won't effect the hitbox or layout of the element, only the rendering.
    pub fn with_transformation(mut self, transformation: Transformation) -> Self {
        self.transformation = Some(transformation);
        self
    }

    /// Set the alpha compositing mode without changing rasterization or caching.
    /// Other platforms already preserve coverage in their SVG sprite shaders.
    pub fn with_alpha_mode(mut self, alpha_mode: SvgAlphaMode) -> Self {
        self.alpha_mode = alpha_mode;
        self
    }
}

impl Element for Svg {
    type RequestLayoutState = ();
    type PrepaintState = Option<Hitbox>;

    fn id(&self) -> Option<crate::ElementId> {
        self.interactivity.element_id.clone()
    }

    fn source_location(&self) -> Option<&'static std::panic::Location<'static>> {
        self.interactivity.source_location()
    }

    fn request_layout(
        &mut self,
        global_id: Option<&GlobalElementId>,
        inspector_id: Option<&InspectorElementId>,
        window: &mut Window,
        cx: &mut App,
    ) -> (LayoutId, Self::RequestLayoutState) {
        let layout_id = self.interactivity.request_layout(
            global_id,
            inspector_id,
            window,
            cx,
            |style, window, cx| window.request_layout(style, None, cx),
        );
        (layout_id, ())
    }

    fn prepaint(
        &mut self,
        global_id: Option<&GlobalElementId>,
        inspector_id: Option<&InspectorElementId>,
        bounds: Bounds<Pixels>,
        _request_layout: &mut Self::RequestLayoutState,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<Hitbox> {
        self.interactivity.prepaint(
            global_id,
            inspector_id,
            bounds,
            bounds.size,
            window,
            cx,
            |_, _, hitbox, _, _| hitbox,
        )
    }

    fn paint(
        &mut self,
        global_id: Option<&GlobalElementId>,
        inspector_id: Option<&InspectorElementId>,
        bounds: Bounds<Pixels>,
        _request_layout: &mut Self::RequestLayoutState,
        hitbox: &mut Option<Hitbox>,
        window: &mut Window,
        cx: &mut App,
    ) where
        Self: Sized,
    {
        self.interactivity.paint(
            global_id,
            inspector_id,
            bounds,
            hitbox.as_ref(),
            window,
            cx,
            |style, window, cx| {
                if let Some((path, color)) = self.path.as_ref().zip(style.text.color) {
                    let transformation = self
                        .transformation
                        .as_ref()
                        .map(|transformation| {
                            transformation.into_matrix(bounds.center(), window.scale_factor())
                        })
                        .unwrap_or_default();

                    window
                        .paint_svg_with_alpha_mode(
                            bounds,
                            path.clone(),
                            transformation,
                            color,
                            self.alpha_mode,
                            cx,
                        )
                        .log_err();
                }
            },
        )
    }
}

impl IntoElement for Svg {
    type Element = Self;

    fn into_element(self) -> Self::Element {
        self
    }
}

impl Styled for Svg {
    fn style(&mut self) -> &mut StyleRefinement {
        &mut self.interactivity.base_style
    }
}

impl InteractiveElement for Svg {
    fn interactivity(&mut self) -> &mut Interactivity {
        &mut self.interactivity
    }
}

/// A transformation to apply to an SVG element.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Transformation {
    scale: Size<f32>,
    translate: Point<Pixels>,
    rotate: Radians,
}

impl Default for Transformation {
    fn default() -> Self {
        Self {
            scale: size(1.0, 1.0),
            translate: point(px(0.0), px(0.0)),
            rotate: radians(0.0),
        }
    }
}

impl Transformation {
    /// Create a new Transformation with the specified scale along each axis.
    pub fn scale(scale: Size<f32>) -> Self {
        Self {
            scale,
            translate: point(px(0.0), px(0.0)),
            rotate: radians(0.0),
        }
    }

    /// Create a new Transformation with the specified translation.
    pub fn translate(translate: Point<Pixels>) -> Self {
        Self {
            scale: size(1.0, 1.0),
            translate,
            rotate: radians(0.0),
        }
    }

    /// Create a new Transformation with the specified rotation in radians.
    pub fn rotate(rotate: impl Into<Radians>) -> Self {
        let rotate = rotate.into();
        Self {
            scale: size(1.0, 1.0),
            translate: point(px(0.0), px(0.0)),
            rotate,
        }
    }

    /// Update the scaling factor of this transformation.
    pub fn with_scaling(mut self, scale: Size<f32>) -> Self {
        self.scale = scale;
        self
    }

    /// Update the translation value of this transformation.
    pub fn with_translation(mut self, translate: Point<Pixels>) -> Self {
        self.translate = translate;
        self
    }

    /// Update the rotation angle of this transformation.
    pub fn with_rotation(mut self, rotate: impl Into<Radians>) -> Self {
        self.rotate = rotate.into();
        self
    }

    fn into_matrix(self, center: Point<Pixels>, scale_factor: f32) -> TransformationMatrix {
        //Note: if you read this as a sequence of matrix multiplications, start from the bottom
        TransformationMatrix::unit()
            .translate(center.scale(scale_factor) + self.translate.scale(scale_factor))
            .rotate(self.rotate)
            .scale(self.scale)
            .translate(center.scale(scale_factor).negate())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{MonochromeSprite, RenderSvgParams};
    use std::mem::{offset_of, size_of};

    #[test]
    fn svg_alpha_mode_preserves_default_and_sprite_layout() {
        assert_eq!(svg().alpha_mode, SvgAlphaMode::ContrastCorrected);
        assert_eq!(SvgAlphaMode::ContrastCorrected as u32, 0);
        assert_eq!(SvgAlphaMode::Coverage as u32, 1);
        assert_eq!(offset_of!(MonochromeSprite, alpha_mode), 4);
        assert_eq!(offset_of!(MonochromeSprite, bounds), 8);
        assert_eq!(size_of::<SvgAlphaMode>(), size_of::<u32>());
    }

    #[test]
    fn svg_alpha_mode_keeps_raster_identity_and_gpu_translation() {
        let transform = Transformation::translate(point(px(0.375), px(0.0)));
        let original = svg()
            .path("brand/mouse-guide.svg")
            .with_transformation(transform);
        let coverage = svg()
            .path("brand/mouse-guide.svg")
            .with_transformation(transform)
            .with_alpha_mode(SvgAlphaMode::Coverage);
        assert_eq!(coverage.alpha_mode, SvgAlphaMode::Coverage);
        assert_eq!(original.transformation, coverage.transformation);
        let raster = |svg: Svg| RenderSvgParams {
            path: svg.path.unwrap(),
            size: size(120.into(), 176.into()),
        };
        assert!(raster(original) == raster(coverage));
    }
}
