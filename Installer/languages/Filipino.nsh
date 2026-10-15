; Filipino MUI strings for the pages used by Orbis Stream.
!insertmacro LANGFILE "Filipino" "Filipino" "Filipino" "Filipino"

!ifdef MUI_WELCOMEPAGE
  ${LangFileString} MUI_TEXT_WELCOME_INFO_TITLE "Maligayang pagdating sa pag-install ng $(^NameDA)"
  ${LangFileString} MUI_TEXT_WELCOME_INFO_TEXT "Gagabayan ka sa pag-install ng $(^NameDA).$\r$\n$\r$\nInirerekomendang isara ang lahat ng ibang programa bago magsimula upang ma-update ang mga file nang hindi kailangang i-restart ang computer.$\r$\n$\r$\n$_CLICK"
!endif

!ifdef MUI_LICENSEPAGE
  ${LangFileString} MUI_TEXT_LICENSE_TITLE "Kasunduan sa Lisensya"
  ${LangFileString} MUI_TEXT_LICENSE_SUBTITLE "Basahin ang mga tuntunin ng lisensya bago i-install ang $(^NameDA)."
  ${LangFileString} MUI_INNERTEXT_LICENSE_BOTTOM "Kung tinatanggap mo ang kasunduan, i-click ang Sumasang-ayon Ako upang magpatuloy. Kailangan mong tanggapin ang kasunduan upang i-install ang $(^NameDA)."
  ${LangFileString} MUI_INNERTEXT_LICENSE_BOTTOM_CHECKBOX "Kung tinatanggap mo ang kasunduan, lagyan ng tsek ang kahon sa ibaba. Kailangan mong tanggapin ang kasunduan upang i-install ang $(^NameDA). $_CLICK"
  ${LangFileString} MUI_INNERTEXT_LICENSE_BOTTOM_RADIOBUTTONS "Kung tinatanggap mo ang kasunduan, piliin ang unang opsyon sa ibaba. Kailangan mong tanggapin ang kasunduan upang i-install ang $(^NameDA). $_CLICK"
!endif

!ifdef MUI_LICENSEPAGE | MUI_UNLICENSEPAGE
  ${LangFileString} MUI_INNERTEXT_LICENSE_TOP "Pindutin ang Page Down upang makita ang natitirang bahagi ng kasunduan."
!endif

!ifdef MUI_COMPONENTSPAGE
  ${LangFileString} MUI_TEXT_COMPONENTS_TITLE "Piliin ang mga Bahagi"
  ${LangFileString} MUI_TEXT_COMPONENTS_SUBTITLE "Piliin ang mga bahagi ng $(^NameDA) na nais mong i-install."
!endif

!ifdef MUI_COMPONENTSPAGE | MUI_UNCOMPONENTSPAGE
  ${LangFileString} MUI_INNERTEXT_COMPONENTS_DESCRIPTION_TITLE "Paglalarawan"
  ${LangFileString} MUI_INNERTEXT_COMPONENTS_DESCRIPTION_INFO "Itapat ang mouse sa isang bahagi upang makita ang paglalarawan nito."
!endif

!ifdef MUI_DIRECTORYPAGE
  ${LangFileString} MUI_TEXT_DIRECTORY_TITLE "Piliin ang Lokasyon ng Pag-install"
  ${LangFileString} MUI_TEXT_DIRECTORY_SUBTITLE "Piliin ang folder kung saan i-i-install ang $(^NameDA)."
!endif

!ifdef MUI_INSTFILESPAGE
  ${LangFileString} MUI_TEXT_INSTALLING_TITLE "Ini-install"
  ${LangFileString} MUI_TEXT_INSTALLING_SUBTITLE "Maghintay habang ini-install ang $(^NameDA)."
  ${LangFileString} MUI_TEXT_FINISH_TITLE "Tapos na ang Pag-install"
  ${LangFileString} MUI_TEXT_FINISH_SUBTITLE "Matagumpay na natapos ang pag-install."
  ${LangFileString} MUI_TEXT_ABORT_TITLE "Nahinto ang Pag-install"
  ${LangFileString} MUI_TEXT_ABORT_SUBTITLE "Hindi natapos ang pag-install."
!endif

!ifdef MUI_UNINSTFILESPAGE
  ${LangFileString} MUI_UNTEXT_UNINSTALLING_TITLE "Inaalis"
  ${LangFileString} MUI_UNTEXT_UNINSTALLING_SUBTITLE "Maghintay habang inaalis ang $(^NameDA)."
  ${LangFileString} MUI_UNTEXT_FINISH_TITLE "Tapos na ang Pag-alis"
  ${LangFileString} MUI_UNTEXT_FINISH_SUBTITLE "Matagumpay na natapos ang pag-alis."
  ${LangFileString} MUI_UNTEXT_ABORT_TITLE "Nahinto ang Pag-alis"
  ${LangFileString} MUI_UNTEXT_ABORT_SUBTITLE "Hindi natapos ang pag-alis."
!endif

!ifdef MUI_FINISHPAGE
  ${LangFileString} MUI_TEXT_FINISH_INFO_TITLE "Pagtatapos ng Pag-install ng $(^NameDA)"
  ${LangFileString} MUI_TEXT_FINISH_INFO_TEXT "Na-install na ang $(^NameDA) sa iyong computer.$\r$\n$\r$\nI-click ang Tapos Na upang isara ang installer."
  ${LangFileString} MUI_TEXT_FINISH_INFO_REBOOT "Kailangang i-restart ang computer upang makumpleto ang pag-install ng $(^NameDA). Gusto mo bang i-restart ngayon?"
!endif

!ifdef MUI_FINISHPAGE | MUI_UNFINISHPAGE
  ${LangFileString} MUI_TEXT_FINISH_REBOOTNOW "I-restart ngayon"
  ${LangFileString} MUI_TEXT_FINISH_REBOOTLATER "Ako na ang magre-restart mamaya"
  ${LangFileString} MUI_TEXT_FINISH_RUN "&Patakbuhin ang $(^NameDA)"
  ${LangFileString} MUI_TEXT_FINISH_SHOWREADME "&Ipakita ang Readme"
  ${LangFileString} MUI_BUTTONTEXT_FINISH "&Tapos Na"
!endif

!ifdef MUI_UNCONFIRMPAGE
  ${LangFileString} MUI_UNTEXT_CONFIRM_TITLE "Alisin ang $(^NameDA)"
  ${LangFileString} MUI_UNTEXT_CONFIRM_SUBTITLE "Alisin ang $(^NameDA) sa iyong computer."
!endif

!ifdef MUI_ABORTWARNING
  ${LangFileString} MUI_TEXT_ABORTWARNING "Sigurado ka bang gusto mong ihinto ang pag-install ng $(^Name)?"
!endif
