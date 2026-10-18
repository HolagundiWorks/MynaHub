//! A small JSON-schema checker for tool arguments: objects, strings, integers and booleans, with `required`,
//! `additionalProperties: false`, `enum`, `maxLength`, `minimum` and `maximum`. That is all the tools use. Anything else in a
//! schema is ignored, which is why the tools' schemas are kept to these.

use serde_json::Value;

pub fn validate(schema: &Value, value: &Value) -> Result<(), String> {
    check(schema, value, "the arguments")
}

fn kind(value: &Value) -> &'static str {
    match value {
        Value::Null => "nothing",
        Value::Bool(_) => "a true or false value",
        Value::Number(_) => "a number",
        Value::String(_) => "text",
        Value::Array(_) => "a list",
        Value::Object(_) => "an object",
    }
}

fn check(schema: &Value, value: &Value, at: &str) -> Result<(), String> {
    match schema.get("type").and_then(Value::as_str) {
        Some("object") => {
            let Some(object) = value.as_object() else {
                return Err(format!("{at} must be an object, not {}", kind(value)));
            };
            let properties = schema.get("properties").and_then(Value::as_object);
            if let Some(required) = schema.get("required").and_then(Value::as_array) {
                for name in required.iter().filter_map(Value::as_str) {
                    if !object.contains_key(name) {
                        return Err(format!("{name} is required"));
                    }
                }
            }
            for (name, item) in object {
                match properties.and_then(|p| p.get(name)) {
                    Some(inner) => check(inner, item, name)?,
                    None if schema.get("additionalProperties") == Some(&Value::Bool(false)) => return Err(format!("{name} is not an argument of this tool")),
                    None => {}
                }
            }
            Ok(())
        }
        Some("string") => {
            let Some(text) = value.as_str() else {
                return Err(format!("{at} must be text, not {}", kind(value)));
            };
            if let Some(max) = schema.get("maxLength").and_then(Value::as_u64) {
                if text.chars().count() as u64 > max {
                    return Err(format!("{at} is longer than {max} characters"));
                }
            }
            if let Some(allowed) = schema.get("enum").and_then(Value::as_array) {
                if !allowed.iter().any(|a| a.as_str() == Some(text)) {
                    return Err(format!("{at} must be one of {}", allowed.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(", ")));
                }
            }
            Ok(())
        }
        Some("integer") => {
            let Some(number) = value.as_i64() else {
                return Err(format!("{at} must be a whole number, not {}", kind(value)));
            };
            if schema.get("minimum").and_then(Value::as_i64).is_some_and(|min| number < min) {
                return Err(format!("{at} is too small"));
            }
            if schema.get("maximum").and_then(Value::as_i64).is_some_and(|max| number > max) {
                return Err(format!("{at} is too large"));
            }
            Ok(())
        }
        Some("boolean") => {
            if value.is_boolean() { Ok(()) } else { Err(format!("{at} must be true or false, not {}", kind(value))) }
        }
        _ => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn arguments_are_checked_against_the_schema() {
        let schema = json!({ "type": "object", "properties": {
            "q": { "type": "string", "maxLength": 3 }, "n": { "type": "integer", "minimum": 1, "maximum": 5 }, "k": { "type": "string", "enum": ["a", "b"] }, "f": { "type": "boolean" }
        }, "required": ["n"], "additionalProperties": false });
        assert!(validate(&schema, &json!({ "n": 2, "q": "abc", "k": "a", "f": true })).is_ok());
        for bad in [json!({}), json!({ "n": 0 }), json!({ "n": 6 }), json!({ "n": 1.5 }), json!({ "n": "2" }), json!({ "n": 2, "q": "abcd" }), json!({ "n": 2, "k": "c" }), json!({ "n": 2, "f": "yes" }), json!({ "n": 2, "extra": 1 }), json!([1]), json!("x")] {
            assert!(validate(&schema, &bad).is_err(), "{bad}");
        }
    }
}
