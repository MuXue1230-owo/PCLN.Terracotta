use std::io;

use clap::Parser;
use terracotta_helper::{bootstrap::SecretToken, cli::Args};

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_target(true)
        .with_writer(io::stderr)
        .init();

    let args = match Args::parse().validate() {
        Ok(value) => value,
        Err(error) => exit_with_error(error),
    };
    let secret = match SecretToken::read_from(io::stdin().lock()) {
        Ok(value) => value,
        Err(error) => exit_with_error(error),
    };

    match terracotta_helper::run(args, secret).await {
        Ok(reason) => tracing::info!(?reason, "Terracotta Helper stopped"),
        Err(error) => exit_with_error(error),
    }
}

fn exit_with_error(error: terracotta_helper::error::HelperError) -> ! {
    tracing::error!(error = %error, "Terracotta Helper failed");
    std::process::exit(error.exit_code().into());
}
