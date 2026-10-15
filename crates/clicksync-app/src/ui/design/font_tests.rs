use super::*;
use ttf_parser::Face;

#[test]
fn mixed_script_font_order_is_language_independent_and_preserves_tabular_digits() {
    let font = ui_font();
    assert_eq!(font.family.as_ref(), DISPLAY_FONT);
    assert_eq!(CHINESE_FONT, "Microsoft YaHei UI");
    assert_eq!(
        font.fallbacks.as_ref().unwrap().fallback_list(),
        [CHINESE_FONT]
    );
    let tabular = tabular_display_font();
    assert_eq!(tabular.family, font.family);
    assert_eq!(tabular.fallbacks, font.fallbacks);
    assert_eq!(tabular.features.0.as_ref(), &[("tnum".into(), 1)]);
}

#[test]
fn embedded_latin_faces_cover_ascii_at_each_weight() {
    for (bytes, weight) in DISPLAY_FONTS.iter().zip([400, 500, 700]) {
        let face = Face::parse(bytes, 0).expect("valid embedded Space Grotesk font");
        assert_eq!(face.weight().to_number(), weight);
        for ch in ' '..='~' {
            assert!(face.glyph_index(ch).is_some(), "missing ASCII {ch:?}");
        }
        for ch in ['中', '文', '，', '。'] {
            assert!(
                face.glyph_index(ch).is_none(),
                "CJK must use system fallback"
            );
        }
    }
}
