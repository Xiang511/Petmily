namespace PawsPort.Dtos
{
    /// <summary>
    /// 登入後回傳給前端的精簡使用者資料，只包含顯示與識別所需的欄位。
    /// 完整個資（電話、生日、Email 等）請另外透過會員 API 取得，不放進前端的登入狀態。
    /// </summary>
    public class AuthUserDTO
    {
        public int UserId { get; set; }

        public string Name { get; set; }

        public string Photo { get; set; }

        /// <summary>
        /// 由完整的會員資料轉換為精簡版本
        /// </summary>
        public static AuthUserDTO From(MemberUserDTO user)
        {
            return new AuthUserDTO
            {
                UserId = user.UserId,
                Name = user.Name,
                Photo = user.Photo
            };
        }
    }
}
