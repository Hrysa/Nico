//! Generates shared parse and load methods for validated TOML definitions.
use proc_macro::TokenStream;
use quote::quote;
use syn::{DeriveInput, parse_macro_input};

/// Requires an explicit `DefinitionValidation` implementation on the named type.
/// Use `#[definition(no_load)]` when loading needs extra context.
#[proc_macro_derive(Definition, attributes(definition))]
pub fn derive_definition(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    expand(input)
        .unwrap_or_else(syn::Error::into_compile_error)
        .into()
}

fn expand(input: DeriveInput) -> syn::Result<proc_macro2::TokenStream> {
    if !input.generics.params.is_empty() {
        return Err(syn::Error::new_spanned(
            input.generics,
            "Definition does not support generic types",
        ));
    }
    let mut no_load = false;
    for attribute in &input.attrs {
        if attribute.path().is_ident("definition") {
            attribute.parse_nested_meta(|meta| {
                if meta.path.is_ident("no_load") && !no_load {
                    no_load = true;
                    Ok(())
                } else {
                    Err(meta.error("expected one no_load option"))
                }
            })?;
        }
    }
    let name = input.ident;
    let load = if no_load {
        quote! {}
    } else {
        quote! {
            /// Reads, parses, and validates a bounded TOML file.
            pub fn load(path: &::std::path::Path) -> ::nico_assets::definition::DefinitionResult<Self> {
                <Self as ::nico_assets::definition::Definition>::load_file(path)
            }
        }
    };
    Ok(quote! {
        impl ::nico_assets::definition::Definition for #name {}
        impl #name {
            /// Parses TOML and applies this type's validation rules.
            pub fn parse(text: &str) -> ::nico_assets::definition::DefinitionResult<Self> {
                <Self as ::nico_assets::definition::Definition>::parse_text(text)
            }
            #load
        }
    })
}
