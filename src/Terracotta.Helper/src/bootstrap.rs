use std::io::Read;

use zeroize::{Zeroize, Zeroizing};

use crate::error::HelperError;

pub struct SecretToken(Zeroizing<Vec<u8>>);

impl SecretToken {
    pub fn read_from<R: Read>(reader: R) -> Result<Self, HelperError> {
        let mut bytes = Vec::with_capacity(65);
        let mut limited = reader.take(65);
        limited.read_to_end(&mut bytes).map_err(HelperError::Ipc)?;
        if bytes.len() != 64
            || !bytes
                .iter()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(byte))
        {
            bytes.zeroize();
            return Err(HelperError::InvalidBootstrapSecret);
        }
        Ok(Self(Zeroizing::new(bytes)))
    }

    pub fn as_bytes(&self) -> &[u8] {
        self.0.as_slice()
    }
}

#[cfg(test)]
mod tests {
    use super::SecretToken;

    #[test]
    fn accepts_exactly_64_lowercase_hex_bytes() {
        assert!(SecretToken::read_from("a".repeat(64).as_bytes()).is_ok());
        assert!(SecretToken::read_from("a".repeat(63).as_bytes()).is_err());
        assert!(SecretToken::read_from("a".repeat(65).as_bytes()).is_err());
        assert!(SecretToken::read_from(format!("{}\n", "a".repeat(64)).as_bytes()).is_err());
        assert!(SecretToken::read_from("A".repeat(64).as_bytes()).is_err());
    }
}
