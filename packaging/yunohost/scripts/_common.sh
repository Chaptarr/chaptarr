#!/bin/bash

#=================================================
# COMMON VARIABLES AND CUSTOM HELPERS
#=================================================

chaptarrng_install_runtime() {
    local debian_version debian_codename microsoft_signing_key
    . /etc/os-release
    debian_version="${VERSION_ID%%.*}"
    debian_codename="$VERSION_CODENAME"

    case "$debian_version:$debian_codename" in
        12:bookworm) microsoft_signing_key="microsoft.asc" ;;
        13:trixie) microsoft_signing_key="microsoft-2025.asc" ;;
        *) ynh_die --message="ChaptarrNG requires Debian 12 (bookworm) or Debian 13 (trixie) for the .NET 10 runtime (found Debian $debian_version $debian_codename)." ;;
    esac

    ynh_apt_install_dependencies_from_extra_repository \
        --repo="deb https://packages.microsoft.com/debian/$debian_version/prod $debian_codename main" \
        --package="aspnetcore-runtime-10.0" \
        --key="https://packages.microsoft.com/keys/$microsoft_signing_key"
}

chaptarrng_prepare_service() {
    install -d -o "$app" -g "$app" -m 0700 "$data_dir" "$data_dir/tmp"
    ynh_multimedia_build_main_dir
    ynh_multimedia_addaccess "$app"
    ynh_config_add_nginx
    ynh_config_add_systemd
}

chaptarrng_register_service() {
    yunohost service add "$app" --description="ChaptarrNG audiobook library service"
}

chaptarrng_start_service() {
    ynh_systemctl --service="$app" --action="start"
}
