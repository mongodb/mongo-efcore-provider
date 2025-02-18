#!/usr/bin/env bash

# Environment variables used as input:
# SILK_CLIENT_ID
# SILK_CLIENT_SECRET

declare -r SSDLC_PATH="./artifacts/ssdlc"
mkdir -p "${SSDLC_PATH}"

echo "Downloading augmented sbom from silk"

docker run --platform="linux/amd64" --rm -v ${PWD}:/pwd \
  --env-file ${workdir}/kondukto_credentials.env \
  artifactory.corp.mongodb.com/release-tools-container-registry-public-local/silkbomb:2.0 \
  augment --repo mongodb/mongo-efcore-provider --branch ${branch_name} --sbom-in /pwd/sbom.json --sbom-out /pwd/${SSDLC_PATH}/augmented-sbom.json
