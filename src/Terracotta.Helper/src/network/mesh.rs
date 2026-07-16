use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};

use sha2::{Digest, Sha256};

use super::credentials::compact_room_code;

/// Fixed host virtual address inside the EasyTier network.
pub const HOST_VIRTUAL_IPV4: Ipv4Addr = Ipv4Addr::new(10, 144, 144, 1);

/// Deterministic mesh service endpoints derived only from the room code so
/// members can locate the host without a side channel.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MeshEndpoints {
    pub scaffolding_port: u16,
    pub minecraft_port: u16,
    pub member_local_scaffolding_port: u16,
    pub member_local_minecraft_port: u16,
}

impl MeshEndpoints {
    pub fn from_room_code(room_code: &str) -> Self {
        let compact = compact_room_code(room_code);
        let digest = Sha256::digest(compact.as_bytes());
        let a = u16::from_be_bytes([digest[0], digest[1]]);
        let b = u16::from_be_bytes([digest[2], digest[3]]);
        // Keep ports out of well-known ranges and avoid zero.
        let scaffolding_port = 41_000 + (a % 9_000);
        let minecraft_port = 51_000 + (b % 9_000);
        // Member-side EasyTier `--port-forward` bind ports (also deterministic).
        let member_local_scaffolding_port = 42_000 + (a % 9_000);
        let member_local_minecraft_port = 52_000 + (b % 9_000);
        Self {
            scaffolding_port,
            minecraft_port,
            member_local_scaffolding_port,
            member_local_minecraft_port,
        }
    }

    pub fn host_scaffolding_addr(self) -> SocketAddr {
        SocketAddr::V4(SocketAddrV4::new(HOST_VIRTUAL_IPV4, self.scaffolding_port))
    }

    pub fn host_minecraft_addr(self) -> SocketAddr {
        SocketAddr::V4(SocketAddrV4::new(HOST_VIRTUAL_IPV4, self.minecraft_port))
    }

    pub fn member_local_scaffolding_addr(self) -> SocketAddr {
        SocketAddr::from(([127, 0, 0, 1], self.member_local_scaffolding_port))
    }

    pub fn member_local_minecraft_addr(self) -> SocketAddr {
        SocketAddr::from(([127, 0, 0, 1], self.member_local_minecraft_port))
    }

    /// EasyTier CLI form: `tcp://local/remote`.
    pub fn member_port_forwards(self) -> [String; 2] {
        [
            format!(
                "tcp://127.0.0.1:{}/{}:{}",
                self.member_local_scaffolding_port, HOST_VIRTUAL_IPV4, self.scaffolding_port
            ),
            format!(
                "tcp://127.0.0.1:{}/{}:{}",
                self.member_local_minecraft_port, HOST_VIRTUAL_IPV4, self.minecraft_port
            ),
        ]
    }

    pub fn mesh_ingress_bind_scaffolding(self) -> SocketAddr {
        SocketAddr::from(([0, 0, 0, 0], self.scaffolding_port))
    }

    pub fn mesh_ingress_bind_minecraft(self) -> SocketAddr {
        SocketAddr::from(([0, 0, 0, 0], self.minecraft_port))
    }
}

#[cfg(test)]
mod tests {
    use super::{HOST_VIRTUAL_IPV4, MeshEndpoints};

    #[test]
    fn endpoints_are_stable_for_room_code() {
        let left = MeshEndpoints::from_room_code("AB12-CD34-EF56");
        let right = MeshEndpoints::from_room_code("ab12 cd34 ef56");
        assert_eq!(left, right);
        assert_ne!(left.scaffolding_port, left.minecraft_port);
        assert_ne!(left.member_local_scaffolding_port, left.scaffolding_port);
        assert!(left.scaffolding_port >= 41_000);
        assert!(left.minecraft_port >= 51_000);
    }

    #[test]
    fn port_forward_uris_target_host_virtual_ip() {
        let endpoints = MeshEndpoints::from_room_code("ZZ99-YY88-XX77");
        let forwards = endpoints.member_port_forwards();
        assert!(forwards[0].starts_with("tcp://127.0.0.1:"));
        assert!(forwards[0].contains(&format!("{HOST_VIRTUAL_IPV4}:")));
        assert!(forwards[1].contains(&endpoints.minecraft_port.to_string()));
    }
}
