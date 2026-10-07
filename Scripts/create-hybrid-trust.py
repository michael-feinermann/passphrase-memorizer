#!/usr/bin/env python3
"""Create product-bound public trust pins without reading any private key."""
import argparse
import importlib.util
import pathlib
import shutil
import sys
import tempfile

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("hybrid_verifier", pathlib.Path(__file__).with_name("verify-hybrid-signatures.py"))
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rsa-spki", required=True, type=pathlib.Path, help="Public RSA-4096 SubjectPublicKeyInfo DER")
    parser.add_argument("--mldsa-public-key", required=True, type=pathlib.Path, help="Public raw ML-DSA-87 key, exactly 2592 bytes")
    parser.add_argument("--certificate", required=True, type=pathlib.Path, help="Public self-issued RSA/SHA-512 signing certificate DER")
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--checksum-tool", required=True, type=pathlib.Path)
    parser.add_argument("--openssl", default=shutil.which("openssl"))
    args = parser.parse_args()
    try:
        V.require(args.openssl is not None, "OpenSSL 3.5 or newer is required")
        destination = args.output.parent.resolve()
        V.require(args.rsa_spki.parent.resolve() == destination and args.mldsa_public_key.parent.resolve() == destination,
                  "Public key files must be beside the output trust file")
        V.safe_path(args.rsa_spki.name, basename=True)
        V.safe_path(args.mldsa_public_key.name, basename=True)
        rsa = V.read_regular(args.rsa_spki, V.MAX_CERTIFICATE)[0]
        ml = V.read_regular(args.mldsa_public_key, 2592)[0]
        certificate = V.read_regular(args.certificate, V.MAX_CERTIFICATE)[0]
        V.validate_der_sequence(rsa)
        V.mldsa_spki(ml)
        with tempfile.TemporaryDirectory(prefix="passphrase-memorizer-public-trust-") as directory:
            verifier = V.PublicVerifier(args.openssl, args.checksum_tool, directory)
            verifier.check_certificate(certificate, rsa)
            trust = {"schema": "passphrase-memorizer-hybrid-trust-v1", "product_id": V.PRODUCT_ID,
                     "rsa_spki": args.rsa_spki.name, "mldsa_public_key": args.mldsa_public_key.name,
                     "rsa_spki_hashes": verifier.hashes(rsa), "mldsa_public_key_hashes": verifier.hashes(ml)}
        data = V.canonical_json(trust)
        V.parse_trust(data)
        with args.output.open("xb") as output:
            output.write(data)
    except (V.VerificationError, OSError, UnicodeError) as error:
        print("Public trust creation FAILED: " + str(error), file=sys.stderr)
        return 1
    print("Created product-bound public RSA-4096 and ML-DSA-87 trust pins using SHA-256, SHA3-512 and Skein-1024-1024.")
    print("Trust must be authenticated through an independently trusted channel before verifying releases.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
